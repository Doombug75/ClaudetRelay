using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClaudetRelay.Services;

/// <summary>
/// TTS backend that calls a locally running VOICEVOX-compatible engine
/// (VOICEVOX, AivisSpeech, COEIROINK, etc.) via its REST API.
///
/// The user must install VOICEVOX separately — https://voicevox.hiroshiba.jp/
/// Default port: 50021. Configurable in Audio settings.
///
/// API flow:
///   GET  /speakers                          → voice/style list
///   POST /audio_query?speaker={id}&text=…   → audio query JSON
///   POST /synthesis?speaker={id}            → WAV bytes
/// </summary>
public sealed class VoicevoxTtsBackend : ITtsBackend
{
    public string Name => "VOICEVOX";

    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    // Simple cache so GetVoices() doesn't always block the UI thread
    private IReadOnlyList<VoiceEntry>? _voiceCache;

    public VoicevoxTtsBackend(int port = 50021)
    {
        _baseUrl = $"http://localhost:{port}";
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public IReadOnlyList<VoiceEntry> GetVoices()
    {
        if (_voiceCache is not null) return _voiceCache;

        try
        {
            // Synchronous for UI calls; VOICEVOX is on localhost so latency is negligible
            var task = FetchVoicesAsync(default);
            task.Wait(TimeSpan.FromSeconds(5));
            if (task.IsCompletedSuccessfully)
                _voiceCache = task.Result;
        }
        catch { }

        return _voiceCache ?? [];
    }

    public async Task<byte[]> SynthesizeToWavAsync(
        string text,
        string voiceName,
        float  speed = 1.0f,
        CancellationToken ct = default)
    {
        var voices = _voiceCache ?? await FetchVoicesAsync(ct);
        var entry  = voices.FirstOrDefault(v =>
            string.Equals(v.DisplayName, voiceName, StringComparison.OrdinalIgnoreCase))
            ?? (voices.Count > 0 ? voices[0] : null);
        if (entry is null) return [];

        // Step 1: audio_query
        var queryUrl = $"{_baseUrl}/audio_query?speaker={entry.SpeakerId}" +
                       $"&text={Uri.EscapeDataString(text)}";
        using var queryResp = await _http.PostAsync(queryUrl, null, ct);
        queryResp.EnsureSuccessStatusCode();
        var queryJson = await queryResp.Content.ReadAsStringAsync(ct);

        // Inject speedScale if non-default
        if (MathF.Abs(speed - 1.0f) > 0.01f)
        {
            using var qDoc = JsonDocument.Parse(queryJson);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(queryJson);
            if (dict is not null)
            {
                dict["speedScale"] = JsonSerializer.SerializeToElement(speed);
                queryJson = JsonSerializer.Serialize(dict);
            }
        }

        // Step 2: synthesis
        using var body      = new StringContent(queryJson, Encoding.UTF8, "application/json");
        using var synthResp = await _http.PostAsync($"{_baseUrl}/synthesis?speaker={entry.SpeakerId}", body, ct);
        synthResp.EnsureSuccessStatusCode();
        return await synthResp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync($"{_baseUrl}/version", ct);
            if (r.IsSuccessStatusCode) _voiceCache = null; // refresh on reconnect
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Dispose() => _http.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<VoiceEntry>> FetchVoicesAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{_baseUrl}/speakers", ct);
        using var doc = JsonDocument.Parse(json);

        var voices = new List<VoiceEntry>();
        foreach (var speaker in doc.RootElement.EnumerateArray())
        {
            var speakerName = speaker.GetProperty("name").GetString() ?? "";
            foreach (var style in speaker.GetProperty("styles").EnumerateArray())
            {
                var styleName = style.GetProperty("name").GetString() ?? "";
                var styleId   = style.GetProperty("id").GetInt32();
                voices.Add(new VoiceEntry($"{speakerName} ({styleName})", "JA", styleId));
            }
        }
        return voices;
    }
}
