using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ClaudetRelay.Services;

public record OllamaChatMessage(string Role, string Content);

public class OllamaService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    public string CurrentModel { get; set; } = "llama3.2";

    public OllamaService(string baseUrl = "http://localhost:11434")
    {
        _base = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    // ── Verfügbare Modelle ──────────────────────────────────────────────────

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

    // ── Vollständige Antwort (kein Streaming) ──────────────────────────────

    public async Task<string> SendAsync(
        IReadOnlyList<OllamaChatMessage> messages,
        CancellationToken ct = default)
    {
        using var content = BuildContent(CurrentModel, messages, stream: false);
        using var response = await _http.PostAsync($"{_base}/api/chat", content, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement
                  .GetProperty("message")
                  .GetProperty("content")
                  .GetString() ?? string.Empty;
    }

    // ── Streaming – liefert Tokens sobald sie ankommen ────────────────────

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

            if (root.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var tokenEl))
            {
                var token = tokenEl.GetString();
                if (!string.IsNullOrEmpty(token))
                    yield return token;
            }

            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
                yield break;
        }
    }

    // ── Erreichbarkeit prüfen ──────────────────────────────────────────────

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

    // ── Hilfsmethode ──────────────────────────────────────────────────────

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
