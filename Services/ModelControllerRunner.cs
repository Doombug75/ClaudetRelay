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

    // ── Persistent conversation history ───────────────────────────────────
    // Each path maintains its own format so no conversion is needed.
    // ClearHistory() resets all three — call it when the user clicks 🗑 Clear.

    private JsonArray _anthropicHistory = [];   // role/content objects for Anthropic API
    private JsonArray _openAiHistory    = [];   // role/content objects for OpenAI-compat APIs
    private string    _reactConversation = "";  // raw prompt string for ReAct / Ollama

    /// <summary>
    /// Wipes all conversation history so the next RunAsync call starts a fresh session.
    /// Also call this when the user switches the controller model.
    /// </summary>
    public void ClearHistory()
    {
        _anthropicHistory  = [];
        _openAiHistory     = [];
        _reactConversation = "";
        _log("⟳  Controller history cleared");
    }

    /// <summary>A unique key that identifies this runner's configuration.
    /// MainWindow uses it to detect when to recreate the runner.</summary>
    public string ConfigKey => $"{_provider}|{_model}|{_serverUrl}";

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
        // Append this turn's user message to the persistent history.
        _anthropicHistory.Add(new JsonObject { ["role"] = "user", ["content"] = userMessage });

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
                ["messages"]   = _anthropicHistory   // ← full multi-turn history
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
                // Remove the user message we just added so the history stays consistent.
                _anthropicHistory.RemoveAt(_anthropicHistory.Count - 1);
                return sb.ToString();
            }

            var result     = JsonNode.Parse(json)!;
            var stopReason = result["stop_reason"]?.GetValue<string>() ?? "";
            var content    = result["content"]?.AsArray() ?? [];

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

            // Always record the assistant's reply in history (including final turns).
            _anthropicHistory.Add(new JsonObject
            {
                ["role"]    = "assistant",
                ["content"] = result["content"]!.DeepClone()
            });

            if (toolCalls.Count == 0 || stopReason == "end_turn")
                break;

            // Execute tool calls and feed results back.
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

            _anthropicHistory.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
        }

        _log($"■  Controller finished  ({sb.Length} chars)");
        return sb.ToString();
    }

    // ── OpenAI-compatible implementation (Groq, OpenRouter, Mistral) ───────

    private async Task<string> RunOpenAiCompatAsync(
        string userMessage, Action<string> onChunk, CancellationToken ct, string endpoint)
    {
        // Seed the system message once; subsequent calls just append user/assistant turns.
        if (_openAiHistory.Count == 0)
            _openAiHistory.Add(new JsonObject
                { ["role"] = "system", ["content"] = BuildControllerSystemPrompt() });

        _openAiHistory.Add(new JsonObject { ["role"] = "user", ["content"] = userMessage });

        var sb = new StringBuilder();
        const int maxRounds = 20;

        for (int round = 0; round < maxRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"]    = _model,
                ["tools"]    = BuildOpenAiToolsArray(),
                ["messages"] = _openAiHistory   // ← full multi-turn history
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
                // Roll back the user message so history stays consistent.
                _openAiHistory.RemoveAt(_openAiHistory.Count - 1);
                return sb.ToString();
            }

            var result       = JsonNode.Parse(json)!;
            var choice       = result["choices"]?[0];
            var message      = choice?["message"];
            var finishReason = choice?["finish_reason"]?.GetValue<string>() ?? "";

            var text      = message?["content"]?.GetValue<string>() ?? "";
            var toolCalls = message?["tool_calls"]?.AsArray();

            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(text);
                onChunk(text);
            }

            if (toolCalls is null || toolCalls.Count == 0 || finishReason == "stop")
            {
                // Record final assistant reply in history.
                _openAiHistory.Add(new JsonObject
                    { ["role"] = "assistant", ["content"] = text });
                break;
            }

            // Record assistant message with tool calls.
            _openAiHistory.Add(new JsonObject
            {
                ["role"]       = "assistant",
                ["content"]    = text,
                ["tool_calls"] = toolCalls.DeepClone()
            });

            // Execute each tool call and record results.
            foreach (var call in toolCalls)
            {
                var callId = call?["id"]?.GetValue<string>() ?? "";
                var name   = call?["function"]?["name"]?.GetValue<string>() ?? "";
                JsonNode inputNode;
                try   { inputNode = JsonNode.Parse(call?["function"]?["arguments"]?.GetValue<string>() ?? "{}") ?? new JsonObject(); }
                catch { inputNode = new JsonObject(); }

                onChunk($"\n⚙ Calling {name}…\n");
                _log($"   controller → tool  {name}");
                var toolResult = await _executeTool(name, inputNode, ct);
                _log($"   controller ← tool  {name}  ({toolResult.Length} chars)");

                _openAiHistory.Add(new JsonObject
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
        // The system prompt already lists all tools with signatures.
        // For ReAct models (Ollama / small local models) we also add:
        //   - the exact XML tag format they MUST use
        //   - a worked example of the full workflow
        //   - explicit "do not do this" examples
        var systemPrompt =
            BuildControllerSystemPrompt() + "\n\n" +
            "═══ HOW TO CALL A TOOL ═══\n" +
            "Output EXACTLY this XML — one tag per tool call, nothing else on those lines:\n" +
            "  <tool_call>{\"name\":\"TOOL_NAME\",\"args\":{\"arg1\":\"value1\"}}</tool_call>\n\n" +
            "You will then receive the result:\n" +
            "  <tool_result>...result text...</tool_result>\n\n" +
            "Keep calling tools until you have everything you need, then write a short summary.\n\n" +

            "═══ WORKED EXAMPLE ═══\n" +
            "User: Write a Python hello-world script to the project folder.\n\n" +
            "CORRECT response (use tools, never write the file content into the chat):\n" +
            "  I'll discover the available folders first.\n" +
            "  <tool_call>{\"name\":\"bridge_list_folders\",\"args\":{}}</tool_call>\n" +
            "  <tool_result>C:\\project (write-enabled)</tool_result>\n" +
            "  <tool_call>{\"name\":\"bridge_write_file\",\"args\":{\"path\":\"C:\\\\project\\\\hello.py\",\"content\":\"print('Hello, world!')\"}}</tool_call>\n" +
            "  <tool_result>File written: C:\\project\\hello.py (22 bytes)</tool_result>\n" +
            "  Done. hello.py has been written to C:\\project.\n\n" +
            "WRONG response (never do this — outputting file content to the chat):\n" +
            "  Here is the file:\n" +
            "  ```python\n" +
            "  print('Hello, world!')\n" +
            "  ```\n\n" +
            "═══ EXAMPLE: DELEGATE TO AN AGENT ═══\n" +
            "  <tool_call>{\"name\":\"bridge_list_agents\",\"args\":{}}</tool_call>\n" +
            "  <tool_result>CodeAgent, ReviewAgent</tool_result>\n" +
            "  <tool_call>{\"name\":\"bridge_get_temp_workspace\",\"args\":{}}</tool_call>\n" +
            "  <tool_result>Temp workspace: C:\\tmp</tool_result>\n" +
            "  <tool_call>{\"name\":\"bridge_run_agent_task\",\"args\":{\"name\":\"CodeAgent\",\"message\":\"Write a Python sort function\",\"output_file\":\"sort.py\"}}</tool_call>\n" +
            "  <tool_result>task_id:abc123 status:running</tool_result>\n" +
            "  <tool_call>{\"name\":\"bridge_wait_for_tasks\",\"args\":{\"task_ids\":[\"abc123\"]}}</tool_call>\n" +
            "  <tool_result>abc123: completed → C:\\tmp\\sort.py</tool_result>\n" +
            "  Done. CodeAgent wrote sort.py to C:\\tmp.";

        // Seed the conversation with the full system prompt once; subsequent turns
        // just append the new user message so context accumulates naturally.
        if (string.IsNullOrEmpty(_reactConversation))
            _reactConversation = systemPrompt + "\n\n";

        _reactConversation += $"User: {userMessage}\nAssistant:";
        var sb = new StringBuilder();

        var baseUrl = string.IsNullOrEmpty(_serverUrl)
            ? "http://localhost:11434" : _serverUrl.TrimEnd('/');

        const int maxRounds = 10;
        for (int round = 0; round < maxRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"]  = _model,
                ["prompt"] = _reactConversation,   // ← full persistent history
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
                // No tool call — final answer for this turn.
                var finalText = response.Replace("<tool_call>", "").Trim();
                sb.Append(finalText);
                onChunk(finalText);
                // Record the completed turn so next call has full context.
                _reactConversation += finalText + "\n\n";
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

            _reactConversation += response + $"\n<tool_result>{toolResult}</tool_result>\n";
        }

        _log($"■  Controller finished  ({sb.Length} chars)");
        return sb.ToString();
    }

    // ── Shared system prompt ───────────────────────────────────────────────

    /// <summary>
    /// Builds the controller system prompt.
    /// Includes argument signatures extracted from InputSchemaOverride so that
    /// small local models (Gemma, Llama, etc.) know exactly how to call each tool.
    /// </summary>
    private string BuildControllerSystemPrompt()
    {
        // Build a compact tool reference: "name(arg1, arg2): description"
        var toolLines = new System.Text.StringBuilder();
        foreach (var t in _tools)
        {
            var argSig = "";
            if (t.InputSchemaOverride is not null)
            {
                try
                {
                    var schema = JsonNode.Parse(t.InputSchemaOverride);
                    var props  = schema?["properties"]?.AsObject();
                    if (props is not null && props.Count > 0)
                        argSig = "(" + string.Join(", ", props.Select(kv => kv.Key)) + ")";
                    else
                        argSig = "()";
                }
                catch { argSig = "(…)"; }
            }
            toolLines.AppendLine($"  {t.Name}{argSig}: {t.Description}");
        }

        return
            "You are a Model Controller — a pure ORCHESTRATOR. " +
            "You coordinate agents and file-system tools; you never do the work yourself.\n\n" +

            "═══ ABSOLUTE RULES ═══\n" +
            "1. NEVER write code, scripts, or file content directly into this chat. " +
            "   If a file must be created, call bridge_write_file or ask an agent via bridge_run_agent_task. " +
            "   Writing a file's contents into the chat is WRONG — using the tool is RIGHT.\n" +
            "2. NEVER attempt to answer a coding, writing, or analysis task yourself. " +
            "   Delegate every piece of actual work to an agent.\n" +
            "3. Always call bridge_list_agents at the start of a new task to discover available agents.\n" +
            "4. Always call bridge_list_folders to discover accessible directories before using any path.\n" +
            "5. Always call bridge_get_temp_workspace to get the base path for output files.\n" +
            "6. For each piece of work, call bridge_run_agent_task with a specific output_file path.\n" +
            "7. After firing tasks, call bridge_wait_for_tasks to collect all results.\n" +
            "8. Your final reply to the user is a BRIEF STATUS SUMMARY — " +
            "   not a repetition of the files' contents.\n\n" +

            "═══ TYPICAL WORKFLOW ═══\n" +
            "Step 1: bridge_list_agents()          → discover which agents exist\n" +
            "Step 2: bridge_list_folders()         → discover accessible paths\n" +
            "Step 3: bridge_get_temp_workspace()   → get the base output directory\n" +
            "Step 4: bridge_run_agent_task(name, message, output_file)  → delegate work\n" +
            "Step 5: bridge_wait_for_tasks(task_ids)                    → wait for results\n" +
            "Step 6: bridge_read_file(path)        → inspect output if needed\n" +
            "Step 7: Reply with a short status summary.\n\n" +

            "═══ AVAILABLE TOOLS ═══\n" + toolLines;
    }
}
