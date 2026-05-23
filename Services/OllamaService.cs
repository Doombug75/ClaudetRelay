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

    public string CurrentModel { get; set; } = "llama3.2";
    public string BaseUrl      => _base;

    public OllamaService(string baseUrl = "http://localhost:11434")
    {
        _base = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
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
        Debug.WriteLine($"[OllamaService.SendAsync] Raw response: {body}");

        using var doc = JsonDocument.Parse(body);
        var result = ExtractContent(doc.RootElement);

        if (string.IsNullOrEmpty(result))
            Debug.WriteLine($"[OllamaService.SendAsync] WARNING: No content extracted from response.");

        return result;
    }

    // ── Streaming – yields tokens as they arrive ───────────────────────────

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<OllamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
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

            // Detect qwen3 thinking tokens — log but do not surface in the UI
            if (root.TryGetProperty("message", out var msgForThinking) &&
                msgForThinking.TryGetProperty("thinking", out var thinkingEl))
            {
                var thinking = thinkingEl.GetString();
                if (!string.IsNullOrEmpty(thinking))
                    Debug.WriteLine($"[OllamaService.StreamAsync] <thinking> {thinking}");
            }

            var token = ExtractContent(root);
            if (!string.IsNullOrEmpty(token))
            {
                yield return token;
            }
            else
            {
                // Log lines that carry no visible content (e.g. pure thinking frames, done marker)
                if (root.TryGetProperty("done", out var doneCheck) && doneCheck.GetBoolean())
                {
                    // Expected — last frame; no log noise
                }
                else
                {
                    Debug.WriteLine($"[OllamaService.StreamAsync] No content in line: {line}");
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

    private static StringContent BuildContent(
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
        writer.WriteEndObject();
        writer.Flush();

        return new StringContent(
            Encoding.UTF8.GetString(ms.ToArray()),
            Encoding.UTF8,
            "application/json");
    }

    public void Dispose() => _http.Dispose();
}
