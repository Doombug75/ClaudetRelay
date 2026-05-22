using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ClaudetRelay.Services;

public record ClaudeChatMessage(string Role, string Content);

public class ClaudeService : IDisposable
{
    private const string MessagesUrl  = "https://api.anthropic.com/v1/messages";
    private const string ModelsUrl    = "https://api.anthropic.com/v1/models";
    private const string AnthropicVer = "2023-06-01";

    private readonly HttpClient _http;

    public string CurrentModel { get; set; } = "claude-sonnet-4-20250514";
    public int    MaxTokens    { get; set; } = 4096;

    public ClaudeService(string apiKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Add("x-api-key",          apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version",  AnthropicVer);
    }

    // ── Erreichbarkeit / Key prüfen ────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(ModelsUrl, ct);
            // 200 = gültiger Key  |  401 = ungültiger Key, aber API erreichbar
            return response.StatusCode is System.Net.HttpStatusCode.OK
                                       or System.Net.HttpStatusCode.Unauthorized;
        }
        catch
        {
            return false;
        }
    }

    // ── Vollständige Antwort (kein Streaming) ──────────────────────────────

    public async Task<string> SendAsync(
        IReadOnlyList<ClaudeChatMessage> messages,
        CancellationToken ct = default)
    {
        using var content = BuildContent(messages, stream: false);
        using var response = await _http.PostAsync(MessagesUrl, content, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        // Antwort liegt in content[0].text
        return doc.RootElement
                  .GetProperty("content")[0]
                  .GetProperty("text")
                  .GetString() ?? string.Empty;
    }

    // ── Streaming via SSE ──────────────────────────────────────────────────
    // Anthropic sendet Server-Sent Events:
    //   event: content_block_delta
    //   data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"..."}}

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ClaudeChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = BuildContent(messages, stream: true)
        };

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            // Nur data-Zeilen auswerten; event/comment/leere Zeilen überspringen
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var json = line["data: ".Length..];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl)) continue;

            switch (typeEl.GetString())
            {
                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("text", out var textEl))
                    {
                        var token = textEl.GetString();
                        if (!string.IsNullOrEmpty(token))
                            yield return token;
                    }
                    break;

                case "message_stop":
                    yield break;
            }
        }
    }

    // ── Hilfsmethode ──────────────────────────────────────────────────────

    private StringContent BuildContent(
        IReadOnlyList<ClaudeChatMessage> messages,
        bool stream)
    {
        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        writer.WriteString ("model",      CurrentModel);
        writer.WriteNumber ("max_tokens", MaxTokens);
        writer.WriteBoolean("stream",     stream);
        writer.WriteStartArray("messages");
        foreach (var m in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role",    m.Role);
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
