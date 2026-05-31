using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudetRelay.Services;

// ── Tool descriptor ────────────────────────────────────────────────────────

/// <summary>
/// One AI participant exposed as an MCP tool.
/// The QueryAsync callback is built in MainWindow from the participant's service.
/// </summary>
public class McpTool
{
    /// <summary>MCP tool name — must be unique, alphanumeric + underscores, max 64 chars.</summary>
    public string Name        { get; set; } = "";

    /// <summary>Shown to the MCP client (Claude Desktop) in the tool picker.</summary>
    public string Description { get; set; } = "";

    /// <summary>Provider label for display in the Bridge panel.</summary>
    public string Provider    { get; set; } = "";

    /// <summary>
    /// Optional override for the JSON inputSchema sent to MCP clients.
    /// When null the default single-string "message" schema is used.
    /// </summary>
    public string? InputSchemaOverride { get; set; }

    /// <summary>
    /// Called with the full arguments <see cref="JsonNode"/> from the MCP request.
    /// Takes precedence over <see cref="QueryAsync"/> when set.
    /// Use this for tools that need more than a single "message" string.
    /// </summary>
    public Func<JsonNode, CancellationToken, Task<string>>? ExecuteAsync { get; set; }

    /// <summary>
    /// Simple message-in / response-out callback.
    /// Used when <see cref="ExecuteAsync"/> is null.
    /// </summary>
    public Func<string, CancellationToken, Task<string>>? QueryAsync { get; set; }
}

// ── MCP HTTP + SSE server ──────────────────────────────────────────────────

/// <summary>
/// Lightweight MCP server (Model Context Protocol, spec 2024-11-05) using the
/// HTTP + Server-Sent Events transport.
///
/// Claude Desktop config snippet:
/// <code>
/// {
///   "mcpServers": {
///     "claudetrelay": {
///       "url": "http://localhost:{Port}/sse"
///     }
///   }
/// }
/// </code>
///
/// The server exposes one MCP tool per registered <see cref="McpTool"/>.
/// Each tool accepts a single "message" string argument and returns the
/// AI participant's response as a text content block.
/// </summary>
public sealed class McpServer : IDisposable
{
    // ── Internals ──────────────────────────────────────────────────────────

    private sealed class SseSession(string id)
    {
        public string Id { get; } = id;
        public StreamWriter? Writer { get; set; }
        public readonly ConcurrentQueue<string> Outbox = new();
        public bool Alive { get; set; } = true;
    }

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, SseSession> _sessions = new();
    private readonly List<McpTool> _tools;
    private readonly Action<string> _log;
    private readonly Func<string>?  _getInstructions;
    private CancellationTokenSource? _cts;

    // ── Public API ─────────────────────────────────────────────────────────

    public int  Port      { get; }
    public bool IsRunning => _listener.IsListening;

    /// <param name="getInstructions">
    /// Optional callback invoked on each <c>initialize</c> handshake.
    /// The returned string is included in the MCP response as
    /// <c>result.instructions</c>, which MCP clients inject into the
    /// model's context automatically — no manual config reading needed.
    /// </param>
    public McpServer(int port, IEnumerable<McpTool> tools, Action<string> log,
                     Func<string>? getInstructions = null)
    {
        Port             = port;
        _tools           = tools.ToList();
        _log             = log;
        _getInstructions = getInstructions;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
        _log($"▶  MCP server listening on http://localhost:{Port}/");
        _log($"   Exposing {_tools.Count} tool(s): {string.Join(", ", _tools.Select(t => t.Name))}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        foreach (var s in _sessions.Values) s.Alive = false;
        _sessions.Clear();
        _log("■  MCP server stopped.");
    }

    public void Dispose() => Stop();

    // ── Accept loop ────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = HandleRequestAsync(ctx, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    // ── Request dispatcher ─────────────────────────────────────────────────

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        // CORS headers — Claude Desktop connects from a browser-like context
        ctx.Response.Headers.Set("Access-Control-Allow-Origin",  "*");
        ctx.Response.Headers.Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        ctx.Response.Headers.Set("Access-Control-Allow-Headers", "Content-Type");

        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";

        try
        {
            if (path == "/sse" && ctx.Request.HttpMethod == "GET")
                await HandleSseAsync(ctx, ct);
            else if (path == "/message" && ctx.Request.HttpMethod == "POST")
                await HandleMessageAsync(ctx, ct);
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch (Exception ex)
        {
            // Suppress disposal errors during planned shutdown — they're expected
            if (_cts?.IsCancellationRequested != true)
                _log($"⚠  Request error: {ex.Message}");
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    // ── SSE transport ──────────────────────────────────────────────────────

    private async Task HandleSseAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var sid  = Guid.NewGuid().ToString("N")[..8];
        var resp = ctx.Response;
        resp.ContentType  = "text/event-stream; charset=utf-8";
        resp.SendChunked  = true;
        resp.Headers.Set("Cache-Control", "no-cache");
        resp.Headers.Set("X-Accel-Buffering", "no");

        var session = new SseSession(sid);
        _sessions[sid] = session;
        _log($"→  Client connected  (session {sid})");

        var writer = new StreamWriter(resp.OutputStream, new UTF8Encoding(false),
                                      bufferSize: 1, leaveOpen: true);
        session.Writer = writer;

        try
        {
            // Tell the client where to POST messages
            await writer.WriteAsync($"event: endpoint\ndata: /message?sessionId={sid}\n\n");
            await writer.FlushAsync();

            // Pump outbox while client is connected
            while (!ct.IsCancellationRequested && session.Alive)
            {
                while (session.Outbox.TryDequeue(out var msg))
                {
                    await writer.WriteAsync($"event: message\ndata: {msg}\n\n");
                    await writer.FlushAsync();
                }
                // Keepalive comment every 15 s (prevents proxy timeouts)
                await writer.WriteAsync(": ping\n\n");
                await writer.FlushAsync();
                await Task.Delay(15_000, ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch { /* client disconnected */ }
        finally
        {
            _sessions.TryRemove(sid, out _);
            _log($"←  Client disconnected (session {sid})");
            writer.Dispose();
            resp.Close();
        }
    }

    // ── Message handling ───────────────────────────────────────────────────

    private async Task HandleMessageAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var sid = ctx.Request.QueryString["sessionId"] ?? "";

        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = await sr.ReadToEndAsync(ct);

        // Acknowledge immediately — response arrives via SSE
        ctx.Response.StatusCode = 202;
        ctx.Response.Close();

        if (!_sessions.TryGetValue(sid, out var session))
        {
            _log($"⚠  Message for unknown session {sid} — ignored.");
            return;
        }

        try
        {
            var req    = JsonNode.Parse(body) ?? throw new InvalidOperationException("Null JSON");
            var method = req["method"]?.GetValue<string>() ?? "";
            var id     = req["id"];

            // Notifications (no id) — handle but never reply
            if (id is null)
            {
                if (method == "notifications/initialized")
                    _log("   Client initialized — ready.");
                return;
            }

            JsonNode reply = method switch
            {
                "initialize"  => HandleInitialize(id),
                "ping"        => HandlePing(id),
                "tools/list"  => HandleToolsList(id),
                "tools/call"  => await HandleToolsCallAsync(req, id, ct),
                _             => BuildError(id, -32601, $"Method not found: {method}")
            };

            session.Outbox.Enqueue(reply.ToJsonString());
        }
        catch (Exception ex)
        {
            _log($"⚠  Error processing message: {ex.Message}");
        }
    }

    // ── RPC handlers ───────────────────────────────────────────────────────

    private JsonNode HandleInitialize(JsonNode id)
    {
        _log("   initialize ← handshake");

        var result = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"]    = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"]      = new JsonObject
            {
                ["name"]    = "ClaudetRelay",
                ["version"] = "1.0"
            }
        };

        // Inject folder context so every MCP client knows what paths are available
        // without having to read any config files.
        var instructions = _getInstructions?.Invoke();
        if (!string.IsNullOrWhiteSpace(instructions))
            result["instructions"] = instructions;

        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
    }

    private static JsonNode HandlePing(JsonNode id) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject() };

    private JsonNode HandleToolsList(JsonNode id)
    {
        _log($"   tools/list → {_tools.Count} tool(s)");
        var arr = new JsonArray();
        foreach (var t in _tools)
        {
            JsonNode schema = t.InputSchemaOverride is not null
                ? JsonNode.Parse(t.InputSchemaOverride)!
                : new JsonObject
                {
                    ["type"]       = "object",
                    ["properties"] = new JsonObject
                    {
                        ["message"] = new JsonObject
                        {
                            ["type"]        = "string",
                            ["description"] = "The message or prompt to send to this AI"
                        }
                    },
                    ["required"] = new JsonArray { "message" }
                };

            arr.Add(new JsonObject
            {
                ["name"]        = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = schema
            });
        }
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"]  = new JsonObject { ["tools"] = arr }
        };
    }

    private async Task<JsonNode> HandleToolsCallAsync(
        JsonNode req, JsonNode id, CancellationToken ct)
    {
        var toolName = req["params"]?["name"]?.GetValue<string>() ?? "";

        var tool = _tools.FirstOrDefault(t => t.Name == toolName);
        if (tool is null)
            return BuildError(id, -32602, $"Unknown tool: {toolName}");

        var args    = req["params"]?["arguments"] ?? new JsonObject();
        var message = args["message"]?.GetValue<string>() ?? "";
        _log($"   tools/call  {toolName}  ← \"{Truncate(message.Length > 0 ? message : args.ToJsonString(), 60)}\"");
        try
        {
            string result;
            if (tool.ExecuteAsync is not null)
                result = await tool.ExecuteAsync(args, ct);
            else if (tool.QueryAsync is not null)
                result = await tool.QueryAsync(message, ct);
            else
                return BuildError(id, -32603, $"Tool '{toolName}' has no executor.");
            _log($"   tools/call  {toolName}  → {result.Length} chars");
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id.DeepClone(),
                ["result"]  = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = result }
                    }
                }
            };
        }
        catch (Exception ex)
        {
            _log($"   tools/call  {toolName}  ⚠ {ex.Message}");
            return BuildError(id, -32000, ex.Message);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static JsonNode BuildError(JsonNode id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["error"]   = new JsonObject { ["code"] = code, ["message"] = message }
        };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
