using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ClaudetRelay.Services;

public record OllamaChatMessage(string Role, string Content, string Sender = "");

public class OllamaService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    public string CurrentModel    { get; set; } = "llama3.2";
    public string BaseUrl         => _base;

    /// <summary>
    /// Ollama context window in tokens (num_ctx).
    /// Set to the model's supported context size. 0 = let Ollama use its built-in default (usually 2048 — often too small).
    /// </summary>
    public int NumCtx     { get; set; } = 8192;

    /// <summary>
    /// Max tokens to generate per reply (num_predict).
    /// 0 = use Ollama default. -1 = no limit.
    /// </summary>
    public int NumPredict { get; set; } = 2048;

    /// <summary>Last line of &lt;thinking&gt; text received during the most recent stream.
    /// Updated while streaming; empty if the model doesn't expose thinking.</summary>
    public string LastThinkingText { get; private set; } = "";

    /// <summary>Fired on the streaming thread each time <see cref="LastThinkingText"/> is updated.
    /// Subscribe in the UI layer and marshal to the dispatcher as needed.</summary>
    public event Action<string>? ThinkingUpdated;

    public OllamaService(string baseUrl = "http://localhost:11434")
    {
        _base = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    // ── Available Models ───────────────────────────────────────────────────

    // ── Model metadata (/api/show) ─────────────────────────────────────────

    public record OllamaModelInfo(
        string Family, string ParameterSize, string QuantizationLevel, string Format,
        long   SizeBytes = 0,
        int    ContextLength = 0)
    {
        /// <summary>Human-readable file size, e.g. "3.8 GB".</summary>
        public string SizeDisplay => SizeBytes <= 0 ? "" :
            SizeBytes >= 1_073_741_824 ? $"{SizeBytes / 1_073_741_824.0:F1} GB" :
            SizeBytes >= 1_048_576     ? $"{SizeBytes / 1_048_576.0:F0} MB"     :
                                         $"{SizeBytes / 1024.0:F0} KB";
    }

    /// <summary>
    /// Calls /api/show to get structured metadata for a specific model.
    /// Returns null if the server is unreachable or the model is not found.
    /// </summary>
    public async Task<OllamaModelInfo?> GetModelInfoAsync(string modelName, CancellationToken ct = default)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { name = modelName });
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_base}/api/show", body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("details", out var d)) return null;

            // Size appears at root level in newer Ollama builds
            long size = doc.RootElement.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

            // Context length — check model_info first (Ollama 0.3+: keys like "llama.context_length",
            // "qwen2.context_length", etc.), then fall back to parsing the parameters text field.
            int contextLength = 0;
            if (doc.RootElement.TryGetProperty("model_info", out var mi))
            {
                foreach (var prop in mi.EnumerateObject())
                {
                    if (prop.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        contextLength = prop.Value.GetInt32();
                        break;
                    }
                }
            }
            // Fallback: "parameters" is a plain string that may contain "num_ctx <value>"
            if (contextLength == 0
                && doc.RootElement.TryGetProperty("parameters", out var paramEl)
                && paramEl.GetString() is string paramStr)
            {
                foreach (var line in paramStr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2
                        && parts[0].Equals("num_ctx", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(parts[1], out var nc))
                    {
                        contextLength = nc;
                        break;
                    }
                }
            }

            return new OllamaModelInfo(
                Family:            d.TryGetProperty("family",             out var f)  ? f.GetString()  ?? "" : "",
                ParameterSize:     d.TryGetProperty("parameter_size",     out var ps) ? ps.GetString() ?? "" : "",
                QuantizationLevel: d.TryGetProperty("quantization_level", out var ql) ? ql.GetString() ?? "" : "",
                Format:            d.TryGetProperty("format",             out var fmt)? fmt.GetString()?? "" : "",
                SizeBytes:         size,
                ContextLength:     contextLength);
        }
        catch { return null; }
    }

    // ── Available Models ───────────────────────────────────────────────────

    public async Task<List<string>> GetModelsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{_base}/api/tags", ct);
        using var doc = JsonDocument.Parse(json);

        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var arr))
            foreach (var m in arr.EnumerateArray())
                if (m.TryGetProperty("name", out var name))
                    names.Add(name.GetString() ?? string.Empty);

        return names;
    }

    // ── Full Response (no streaming) ───────────────────────────────────────

    public async Task<string> SendAsync(
        IReadOnlyList<OllamaChatMessage> messages,
        CancellationToken ct = default)
    {
        using var content = BuildContent(CurrentModel, messages, stream: false);
        using var response = await _http.PostAsync($"{_base}/api/chat", content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
#if DEBUG
        Debug.WriteLine($"[OllamaService.SendAsync] Raw response: {body}");
#endif

        using var doc = JsonDocument.Parse(body);
        var result = ExtractContent(doc.RootElement);

#if DEBUG
        if (string.IsNullOrEmpty(result))
            Debug.WriteLine($"[OllamaService.SendAsync] WARNING: No content extracted from response.");
#endif

        return result;
    }

    // ── Streaming – yields tokens as they arrive ───────────────────────────

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<OllamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastThinkingText = "";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/chat")
        {
            Content = BuildContent(CurrentModel, messages, stream: true)
        };

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Detect qwen3 / thinking-model tokens — store last line for tooltip
            if (root.TryGetProperty("message", out var msgForThinking) &&
                msgForThinking.TryGetProperty("thinking", out var thinkingEl))
            {
                var thinking = thinkingEl.GetString();
                if (!string.IsNullOrEmpty(thinking))
                {
#if DEBUG
                    Debug.WriteLine($"[OllamaService.StreamAsync] <thinking> {thinking}");
#endif
                    // Keep the last non-empty line as the tooltip hint
                    var lines = thinking.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        var last = lines[^1].Trim();
                        if (!string.IsNullOrEmpty(last))
                        {
                            LastThinkingText = last.Length > 120 ? last[..120] + "…" : last;
                            ThinkingUpdated?.Invoke(LastThinkingText);
                        }
                    }
                }
            }

            var token = ExtractContent(root);
            if (!string.IsNullOrEmpty(token))
            {
                yield return token;
            }
            else
            {
                if (root.TryGetProperty("done", out var doneCheck) && doneCheck.GetBoolean())
                {
                    // Expected — last frame; no log noise
                }
                else
                {
#if DEBUG
                    Debug.WriteLine($"[OllamaService.StreamAsync] No content in line: {line}");
#endif
                }
            }

            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
                yield break;
        }
    }

    // ── Availability check ─────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{_base}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Content extraction helper ──────────────────────────────────────────
    // Handles multiple response formats:
    //   1. Standard Ollama:  { "message": { "content": "..." } }
    //   2. Content-block:    { "message": { "content": [{"type":"text","text":"..."}] } }
    //   3. Root response:    { "response": "..." }          (generate API fallback)
    //   4. Root content:     { "content": "..." }           (some custom backends)

    private static string ExtractContent(JsonElement root)
    {
        // 1 & 2 — message.content (string or array)
        if (root.TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var contentEl))
        {
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                var text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            else if (contentEl.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in contentEl.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var typeEl) &&
                        typeEl.GetString() == "text" &&
                        block.TryGetProperty("text", out var textEl))
                    {
                        sb.Append(textEl.GetString());
                    }
                }
                var result = sb.ToString();
                if (!string.IsNullOrEmpty(result)) return result;
            }
        }

        // 3 — root-level "response" field (Ollama /api/generate style)
        if (root.TryGetProperty("response", out var responseEl))
        {
            var text = responseEl.GetString();
            if (!string.IsNullOrEmpty(text)) return text;
        }

        // 4 — root-level "content" field
        if (root.TryGetProperty("content", out var rootContentEl))
        {
            var text = rootContentEl.GetString();
            if (!string.IsNullOrEmpty(text)) return text;
        }

        return string.Empty;
    }

    // ── Helper ─────────────────────────────────────────────────────────────

    private StringContent BuildContent(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        bool stream)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteBoolean("stream", stream);
        writer.WriteStartArray("messages");
        foreach (var m in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", m.Role);
            writer.WriteString("content", m.Content);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Emit options block — num_ctx prevents context-window exhaustion in long conversations.
        // Both values are only written when non-zero so callers can opt out by leaving them at 0.
        if (NumCtx > 0 || NumPredict > 0)
        {
            writer.WriteStartObject("options");
            if (NumCtx     > 0) writer.WriteNumber("num_ctx",     NumCtx);
            if (NumPredict > 0) writer.WriteNumber("num_predict", NumPredict);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();

        return new StringContent(
            Encoding.UTF8.GetString(ms.ToArray()),
            Encoding.UTF8,
            "application/json");
    }

    public void Dispose() => _http.Dispose();
}
