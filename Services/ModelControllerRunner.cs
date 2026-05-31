using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudetRelay.Services;

/// <summary>
/// Runs the Model Controller agentic loop.
/// Sends the user message + tool schemas to the controller API model,
/// executes any tool calls it makes, feeds results back, and repeats
/// until the model produces a final text response — exactly like an
/// MCP client (Claude Desktop / Claude Code) does, but built-in.
///
/// Supported controller providers: Anthropic, OpenAI-compatible (Groq, OpenRouter, Mistral).
/// Ollama and Gemini fall back to a ReAct-style prompt approach.
/// </summary>
public sealed class ModelControllerRunner
{
    // ── Dependencies ───────────────────────────────────────────────────────

    private readonly string               _provider;
    private readonly string               _model;
    private readonly string               _apiKey;
    private readonly string               _serverUrl;   // for Ollama
    private readonly List<McpTool>        _tools;
    private readonly Action<string>       _log;         // activity log
    private readonly Func<string, JsonNode, CancellationToken, Task<string>> _executeTool;
    private static readonly HttpClient    _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public ModelControllerRunner(
        string provider, string model, string apiKey, string serverUrl,
        IEnumerable<McpTool> tools,
        Func<string, JsonNode, CancellationToken, Task<string>> executeTool,
        Action<string> log)
    {
        _provider    = provider;
        _model       = model;
        _apiKey      = apiKey;
        _serverUrl   = serverUrl;
        _tools       = tools.ToList();
        _executeTool = executeTool;
        _log         = log;
    }

    // ── Public entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Runs the full agentic loop for one user turn.
    /// Calls <paramref name="onChunk"/> with streamed text as it arrives,
    /// and with tool-call status lines prefixed with "⚙ ".
    /// Returns the complete final text response.
    /// </summary>
    public async Task<string> RunAsync(
        string userMessage, Action<string> onChunk, CancellationToken ct)
    {
        _log($"▶  Controller  [{_provider} / {_model}]  ← user message ({userMessage.Length} chars)");

        return _provider.ToLowerInvariant() switch
        {
            "anthropic"  => await RunAnthropicAsync(userMessage, onChunk, ct),
            "groq"       => await RunOpenAiCompatAsync(userMessage, onChunk, ct,
                                "https://api.groq.com/openai/v1/chat/completions"),
            "openrouter" => await RunOpenAiCompatAsync(userMessage, onChunk, ct,
                                "https://openrouter.ai/api/v1/chat/completions"),
            "mistral"    => await RunOpenAiCompatAsync(userMessage, onChunk, ct,
                                "https://api.mistral.ai/v1/chat/completions"),
            "ollama"     => await RunReActAsync(userMessage, onChunk, ct),
            _            => await RunReActAsync(userMessage, onChunk, ct)
        };
    }

    // ── Tool schema helpers ────────────────────────────────────────────────

    private JsonArray BuildAnthropicToolsArray()
    {
        var arr = new JsonArray();
        foreach (var t in _tools)
        {
            var schema = t.InputSchemaOverride is not null
                ? JsonNode.Parse(t.InputSchemaOverride)!
                : new JsonObject
                {
                    ["type"]       = "object",
                    ["properties"] = new JsonObject
                    {
                        ["message"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray { "message" }
                };
            arr.Add(new JsonObject
            {
                ["name"]         = t.Name,
                ["description"]  = t.Description,
                ["input_schema"] = schema
            });
        }
        return arr;
    }

    private JsonArray BuildOpenAiToolsArray()
    {
        var arr = new JsonArray();
        foreach (var t in _tools)
        {
            var schema = t.InputSchemaOverride is not null
                ? JsonNode.Parse(t.InputSchemaOverride)!
                : new JsonObject
                {
                    ["type"]       = "object",
                    ["properties"] = new JsonObject
                    {
                        ["message"] = new JsonObject { ["type"] = "string" }
                    }
                };
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"]        = t.Name,
                    ["description"] = t.Description,
                    ["parameters"]  = schema
                }
            });
        }
        return arr;
    }

    // ── Anthropic implementation ───────────────────────────────────────────

    private async Task<string> RunAnthropicAsync(
        string userMessage, Action<string> onChunk, CancellationToken ct)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userMessage }
        };

        var sb = new StringBuilder();
        const int maxRounds = 20;

        for (int round = 0; round < maxRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"]      = _model,
                ["max_tokens"] = 4096,
                ["system"]     = BuildControllerSystemPrompt(),
                ["tools"]      = BuildAnthropicToolsArray(),
                ["messages"]   = messages
            };

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(body.ToJsonString(),
                Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errMsg = $"Anthropic API error {(int)resp.StatusCode}: {json}";
                _log($"⚠  {errMsg}");
                onChunk($"\n\n❌ {errMsg}");
                return sb.ToString();
            }

            var result     = JsonNode.Parse(json)!;
            var stopReason = result["stop_reason"]?.GetValue<string>() ?? "";
            var content    = result["content"]?.AsArray() ?? [];

            // Collect text chunks and tool calls from this round
            var toolCalls = new List<(string Id, string Name, JsonNode Input)>();

            foreach (var block in content)
            {
                var type = block?["type"]?.GetValue<string>() ?? "";
                if (type == "text")
                {
                    var text = block!["text"]?.GetValue<string>() ?? "";
                    sb.Append(text);
                    onChunk(text);
                }
                else if (type == "tool_use")
                {
                    toolCalls.Add((
                        block!["id"]!.GetValue<string>(),
                        block["name"]!.GetValue<string>(),
                        block["input"] ?? new JsonObject()
                    ));
                }
            }

            if (toolCalls.Count == 0 || stopReason == "end_turn")
                break;

            // Append assistant turn to conversation
            messages.Add(new JsonObject
            {
                ["role"]    = "assistant",
                ["content"] = JsonNode.Parse(json)!["content"]!.DeepClone()
            });

            // Execute each tool call and collect results
            var toolResults = new JsonArray();
            foreach (var (id, name, input) in toolCalls)
            {
                onChunk($"\n⚙ Calling {name}…\n");
                _log($"   controller → tool  {name}");
                var toolResult = await _executeTool(name, input, ct);
                _log($"   controller ← tool  {name}  ({toolResult.Length} chars)");
                toolResults.Add(new JsonObject
                {
                    ["type"]        = "tool_result",
                    ["tool_use_id"] = id,
                    ["content"]     = toolResult
                });
            }

            messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
        }

        _log($"■  Controller finished  ({sb.Length} chars)");
        return sb.ToString();
    }

    // ── OpenAI-compatible implementation (Groq, OpenRouter, Mistral) ───────

    private async Task<string> RunOpenAiCompatAsync(
        string userMessage, Action<string> onChunk, CancellationToken ct, string endpoint)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system",  ["content"] = BuildControllerSystemPrompt() },
            new JsonObject { ["role"] = "user",    ["content"] = userMessage }
        };

        var sb = new StringBuilder();
        const int maxRounds = 20;

        for (int round = 0; round < maxRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"]    = _model,
                ["tools"]    = BuildOpenAiToolsArray(),
                ["messages"] = messages
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(body.ToJsonString(),
                Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errMsg = $"{_provider} API error {(int)resp.StatusCode}: {json}";
                _log($"⚠  {errMsg}");
                onChunk($"\n\n❌ {errMsg}");
                return sb.ToString();
            }

            var result    = JsonNode.Parse(json)!;
            var choice    = result["choices"]?[0];
            var message   = choice?["message"];
            var finishReason = choice?["finish_reason"]?.GetValue<string>() ?? "";

            var text      = message?["content"]?.GetValue<string>() ?? "";
            var toolCalls = message?["tool_calls"]?.AsArray();

            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(text);
                onChunk(text);
            }

            if (toolCalls is null || toolCalls.Count == 0 || finishReason == "stop")
                break;

            // Append assistant message
            messages.Add(new JsonObject
            {
                ["role"]       = "assistant",
                ["content"]    = text,
                ["tool_calls"] = toolCalls.DeepClone()
            });

            // Execute each tool call
            foreach (var call in toolCalls)
            {
                var callId = call?["id"]?.GetValue<string>() ?? "";
                var name   = call?["function"]?["name"]?.GetValue<string>() ?? "";
                JsonNode   inputNode;
                try   { inputNode = JsonNode.Parse(call?["function"]?["arguments"]?.GetValue<string>() ?? "{}") ?? new JsonObject(); }
                catch { inputNode = new JsonObject(); }

                onChunk($"\n⚙ Calling {name}…\n");
                _log($"   controller → tool  {name}");
                var toolResult = await _executeTool(name, inputNode, ct);
                _log($"   controller ← tool  {name}  ({toolResult.Length} chars)");

                messages.Add(new JsonObject
                {
                    ["role"]         = "tool",
                    ["tool_call_id"] = callId,
                    ["content"]      = toolResult
                });
            }
        }

        _log($"■  Controller finished  ({sb.Length} chars)");
        return sb.ToString();
    }

    // ── ReAct fallback (Ollama and unsupported providers) ─────────────────

    private async Task<string> RunReActAsync(
        string userMessage, Action<string> onChunk, CancellationToken ct)
    {
        var toolList = string.Join("\n", _tools.Select(t =>
            $"  {t.Name}: {t.Description}"));

        var systemPrompt =
            BuildControllerSystemPrompt() + "\n\n" +
            "To call a tool, output exactly this format (nothing else on those lines):\n" +
            "<tool_call>{\"name\":\"tool_name\",\"args\":{...}}</tool_call>\n\n" +
            "After each tool result you will receive:\n" +
            "<tool_result>...</tool_result>\n\n" +
            "Repeat tool calls until you have enough information, then write your final answer.\n\n" +
            "Available tools:\n" + toolList;

        var conversation = $"{systemPrompt}\n\nUser: {userMessage}\nAssistant:";
        var sb = new StringBuilder();

        var baseUrl = string.IsNullOrEmpty(_serverUrl)
            ? "http://localhost:11434" : _serverUrl.TrimEnd('/');

        const int maxRounds = 10;
        for (int round = 0; round < maxRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"]  = _model,
                ["prompt"] = conversation,
                ["stream"] = false
            };

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{baseUrl}/api/generate");
            req.Content = new StringContent(body.ToJsonString(),
                Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                onChunk($"\n\n❌ Ollama error {(int)resp.StatusCode}: {json}");
                break;
            }

            var response = JsonNode.Parse(json)?["response"]?.GetValue<string>() ?? "";

            // Parse tool calls
            var toolCallMatch = System.Text.RegularExpressions.Regex.Match(
                response, @"<tool_call>([\s\S]*?)<\/tool_call>");

            if (!toolCallMatch.Success)
            {
                // No tool call — final answer
                var finalText = response.Replace("<tool_call>", "").Trim();
                sb.Append(finalText);
                onChunk(finalText);
                break;
            }

            // Text before tool call
            var beforeTool = response[..toolCallMatch.Index].Trim();
            if (!string.IsNullOrEmpty(beforeTool))
            {
                sb.Append(beforeTool + "\n");
                onChunk(beforeTool + "\n");
            }

            // Execute tool
            string toolResult;
            try
            {
                var callJson  = JsonNode.Parse(toolCallMatch.Groups[1].Value)!;
                var toolName  = callJson["name"]?.GetValue<string>() ?? "";
                var argsNode  = callJson["args"] ?? new JsonObject();
                onChunk($"\n⚙ Calling {toolName}…\n");
                _log($"   controller → tool  {toolName}");
                toolResult = await _executeTool(toolName, argsNode, ct);
                _log($"   controller ← tool  {toolName}  ({toolResult.Length} chars)");
            }
            catch (Exception ex)
            {
                toolResult = $"Error parsing tool call: {ex.Message}";
            }

            conversation += response + $"\n<tool_result>{toolResult}</tool_result>\n";
        }

        _log($"■  Controller finished  ({sb.Length} chars)");
        return sb.ToString();
    }

    // ── Shared system prompt ───────────────────────────────────────────────

    private string BuildControllerSystemPrompt()
    {
        var agentList = string.Join(", ", _tools
            .Where(t => t.Name == "bridge_list_agents")
            .Take(1)
            .Select(_ => "(call bridge_list_agents to discover)"));

        return
            "You are a Model Controller — an AI orchestrator that coordinates a team of local AI agents " +
            "and file-system tools to complete the user's request.\n\n" +
            "Your job:\n" +
            "1. Break the task into steps.\n" +
            "2. Use the available Bridge tools to discover agents and folders, delegate sub-tasks, " +
            "   read and write files, fetch web pages, and run agents in parallel when possible.\n" +
            "3. Synthesise the results into a clear final answer for the user.\n\n" +
            "Guidelines:\n" +
            "- Call bridge_list_agents first to discover available agents.\n" +
            "- Call bridge_list_folders to discover accessible file paths.\n" +
            "- For independent tasks, use bridge_run_agent_task for each and bridge_wait_for_tasks " +
            "  to collect all results before proceeding.\n" +
            "- Always check bridge_wait_for_tasks results for errors — handle timeouts gracefully.\n" +
            "- Write interim results to the temp workspace so agents can share context.\n" +
            "- Be concise in tool calls; be thorough in the final answer.";
    }
}
