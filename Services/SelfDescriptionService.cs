using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClaudetRelay.Services;

/// <summary>
/// Asks a model two questions and saves the results to AppSettings.
/// Designed to be fire-and-forget — never touches WPF UI elements.
///
///   Phase 1  (temperature 0) — deterministic title + one-sentence description.
///   Phase 2  (default temp)  — what the model likes / dislikes doing.
/// </summary>
public static class SelfDescriptionService
{
    // ── Prompts ────────────────────────────────────────────────────────────

    private const string DescriptionPrompt =
        "IMPORTANT: You MUST reply with EXACTLY these two lines. No greeting, no explanation, no extra text — just these two lines.\n" +
        "TITLE: [your role in 1–3 words]\n" +
        "DESC: [one sentence describing what you do best]\n\n" +
        "Example — your entire reply should look exactly like this:\n" +
        "TITLE: Coding Expert\n" +
        "DESC: I excel at writing, debugging, and reviewing code in any language.\n\n" +
        "Now write your two lines:";

    private const string LikesPrompt =
        "IMPORTANT: You MUST reply with EXACTLY these two lines. No greeting, no explanation, no extra text — just these two lines.\n" +
        "LIKES: [one sentence — what kind of work you enjoy most]\n" +
        "DISLIKES: [one sentence — what kind of work you find least interesting]\n\n" +
        "Example — your entire reply should look exactly like this:\n" +
        "LIKES: I love tackling complex algorithmic puzzles and system-design challenges.\n" +
        "DISLIKES: I find purely repetitive boilerplate generation a bit tedious.\n\n" +
        "Now write your two lines:";

    // ── Public API ─────────────────────────────────────────────────────────

    public static async Task FetchAndSaveAsync(
        string type, string model, string serverUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        if (IsCloud(type) && string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(type)))
            return;

        try
        {
            // ── Phase 1: description at temperature 0 ──────────────────────
            var (descRaw, descError) = await CallAsync(type, model, serverUrl,
                                                       DescriptionPrompt, temperature: 0f, ct);

            var s      = SettingsService.Load();
            var target = s.Participants.FirstOrDefault(p =>
                string.Equals(p.Type,  type,  StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;

            if (descError is not null)
            {
                target.LastApiError = $"ERROR:{descError}";
                SettingsService.Save(s);
                return;   // don't bother with phase 2 if the API already rejected us
            }

            if (!string.IsNullOrWhiteSpace(descRaw))
            {
                var parsed = ParseDescription(descRaw);
                if (parsed is not null)
                {
                    target.LastApiError    = "";
                    target.SelfDescription = parsed.Value.Description;
                    if (string.IsNullOrEmpty(target.Role))
                        target.Role = parsed.Value.Title;
                }
            }
            SettingsService.Save(s);

            // ── Phase 2: likes / dislikes at default temperature ───────────
            var (likesRaw, likesError) = await CallAsync(type, model, serverUrl,
                                                         LikesPrompt, temperature: null, ct);
            if (likesError is null && !string.IsNullOrWhiteSpace(likesRaw))
            {
                var (likes, dislikes) = ParseLikes(likesRaw);
                s = SettingsService.Load();
                target = s.Participants.FirstOrDefault(p =>
                    string.Equals(p.Type,  type,  StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                {
                    if (!string.IsNullOrEmpty(likes))    target.Likes    = likes;
                    if (!string.IsNullOrEmpty(dislikes)) target.Dislikes = dislikes;
                    SettingsService.Save(s);
                }
            }
        }
        catch { /* never propagate */ }
    }

    // ── Voice preference API ──────────────────────────────────────────────

    /// <summary>
    /// Asks the model to choose its preferred TTS voice from <paramref name="availableVoices"/>.
    /// Returns the display name of the chosen voice, or null on failure / no match.
    /// The model is expected to reply with exactly: <c>VOICE: &lt;name&gt;</c>
    /// </summary>
    public static async Task<string?> FetchPreferredVoiceAsync(
        string type, string model, string serverUrl,
        IReadOnlyList<string> availableVoices,
        CancellationToken ct = default)
    {
        if (availableVoices.Count == 0) return null;
        if (IsCloud(type) && string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(type)))
            return null;

        var voiceList = string.Join("\n", availableVoices.Select(v => $"  • {v}"));
        var prompt =
            "You are being configured for text-to-speech output in ClaudetRelay. " +
            "Choose the voice below that best fits your personality, communication style, " +
            "and how you would imagine yourself sounding.\n\n" +
            $"Available voices:\n{voiceList}\n\n" +
            "Reply with EXACTLY one line and nothing else:\n" +
            "VOICE: <exact name from the list>";

        try
        {
            var (raw, error) = await CallAsync(type, model, serverUrl, prompt, temperature: 0f, ct);
            if (error is not null || string.IsNullOrWhiteSpace(raw)) return null;

            // Parse "VOICE: <name>" — try exact match first, then case-insensitive, then substring
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (!t.StartsWith("VOICE:", StringComparison.OrdinalIgnoreCase)) continue;
                var chosen = t[6..].Trim().Trim('"', '\'', '*');
                if (string.IsNullOrWhiteSpace(chosen)) continue;

                // Exact
                var exact = availableVoices.FirstOrDefault(v =>
                    string.Equals(v, chosen, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact;

                // Partial — voice name contains the chosen word or vice versa
                var partial = availableVoices.FirstOrDefault(v =>
                    v.Contains(chosen, StringComparison.OrdinalIgnoreCase) ||
                    chosen.Contains(v, StringComparison.OrdinalIgnoreCase));
                return partial;
            }
            return null;
        }
        catch { return null; }
    }

    // ── Mood API ───────────────────────────────────────────────────────────

    private const string MoodPrompt =
        "Reply with exactly one word describing your current mood. " +
        "Just the single word — no punctuation, no explanation.";

    /// <summary>
    /// Asks the model for a one-word mood and returns it, or null on any failure.
    /// Never throws — designed for fire-and-forget callers.
    /// </summary>
    public static async Task<string?> FetchMoodAsync(
        string type, string model, string serverUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (IsCloud(type) && string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(type)))
            return null;
        try
        {
            var (raw, error) = await CallAsync(type, model, serverUrl, MoodPrompt, temperature: null, ct);
            if (error is not null || string.IsNullOrWhiteSpace(raw)) return null;
            // Take the first token that looks like a single word (letters only, 3–20 chars)
            var word = raw.Trim().Split([' ', '\n', '\r', '.', ',', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault(w => w.Length is >= 2 and <= 20 && w.All(char.IsLetter));
            return word is null ? null : char.ToUpper(word[0]) + word[1..].ToLower();
        }
        catch { return null; }
    }

    // ── Parsers ────────────────────────────────────────────────────────────

    private static (string Title, string Description)? ParseDescription(string raw)
    {
        string title = "", desc = "";
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
                title = t[6..].Trim().Trim('[', ']');
            else if (t.StartsWith("DESC:", StringComparison.OrdinalIgnoreCase))
                desc = t[5..].Trim().Trim('[', ']');
        }
        return string.IsNullOrEmpty(title) && string.IsNullOrEmpty(desc) ? null : (title, desc);
    }

    private static (string Likes, string Dislikes) ParseLikes(string raw)
    {
        string likes = "", dislikes = "";
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("LIKES:", StringComparison.OrdinalIgnoreCase))
                likes = t[6..].Trim().Trim('[', ']');
            else if (t.StartsWith("DISLIKES:", StringComparison.OrdinalIgnoreCase))
                dislikes = t[9..].Trim().Trim('[', ']');
        }
        return (likes, dislikes);
    }

    // ── Unified dispatcher ─────────────────────────────────────────────────

    /// <param name="temperature">null = provider default; 0f = fully deterministic.</param>
    private static Task<(string? body, int? errorCode)> CallAsync(
        string type, string model, string serverUrl,
        string prompt, float? temperature, CancellationToken ct) => type switch
    {
        "Ollama"    => CallOllamaAsync(model, serverUrl, prompt, temperature, ct),
        "Anthropic" => CallAnthropicAsync(model, prompt, temperature, ct),
        "Google AI" => CallGoogleAsync(model, prompt, temperature, ct),
        _           => CallOpenAiCompatAsync(type, model, prompt, temperature, ct)
    };

    // ── Per-provider implementations ───────────────────────────────────────

    private static async Task<(string? body, int? errorCode)> CallOllamaAsync(
        string model, string serverUrl, string prompt, float? temperature, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var options = temperature.HasValue
            ? (object)new { temperature = temperature.Value }
            : new { };
        var payload = JsonSerializer.Serialize(new
        {
            model, stream = false,
            messages = new[] { new { role = "user", content = prompt } },
            options
        });
        using var req  = new StringContent(payload, Encoding.UTF8, "application/json");
        var base_      = serverUrl.TrimEnd('/');
        using var resp = await http.PostAsync($"{base_}/api/chat", req, ct);
        if (!resp.IsSuccessStatusCode) return (null, (int)resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("message").GetProperty("content").GetString(), null);
    }

    private static async Task<(string? body, int? errorCode)> CallAnthropicAsync(
        string model, string prompt, float? temperature, CancellationToken ct)
    {
        var key = WindowsCredentialManager.Load("Anthropic") ?? "";
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("x-api-key", key);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var body = temperature.HasValue
            ? JsonSerializer.Serialize(new { model, max_tokens = 150, temperature = (double)temperature.Value,
                                             messages = new[] { new { role = "user", content = prompt } } })
            : JsonSerializer.Serialize(new { model, max_tokens = 150,
                                             messages = new[] { new { role = "user", content = prompt } } });
        using var req  = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync("https://api.anthropic.com/v1/messages", req, ct);
        if (!resp.IsSuccessStatusCode) return (null, (int)resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString(), null);
    }

    private static async Task<(string? body, int? errorCode)> CallGoogleAsync(
        string model, string prompt, float? temperature, CancellationToken ct)
    {
        var key  = WindowsCredentialManager.Load("Google AI") ?? "";
        var slug = model.Contains('/') ? model : $"models/{model}";
        var url  = $"https://generativelanguage.googleapis.com/v1beta/{slug}:generateContent?key={key}";

        using var http = new HttpClient();
        var genCfg = temperature.HasValue
            ? (object)new { maxOutputTokens = 150, temperature = (double)temperature.Value }
            : new { maxOutputTokens = 150 };
        var body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = genCfg
        });
        using var req  = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(url, req, ct);
        if (!resp.IsSuccessStatusCode) return (null, (int)resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("candidates")[0]
                               .GetProperty("content")
                               .GetProperty("parts")[0]
                               .GetProperty("text").GetString(), null);
    }

    private static async Task<(string? body, int? errorCode)> CallOpenAiCompatAsync(
        string type, string model, string prompt, float? temperature, CancellationToken ct)
    {
        var key     = WindowsCredentialManager.Load(type) ?? "";
        var baseUrl = type switch
        {
            "OpenAI ChatGPT" => "https://api.openai.com/v1",
            "Groq"           => "https://api.groq.com/openai/v1",
            "OpenRouter"     => "https://openrouter.ai/api/v1",
            "Mistral"        => "https://api.mistral.ai/v1",
            "xAI Grok"       => "https://api.x.ai/v1",
            "Ollama ☁"       => "https://api.ollama.com/v1",
            _                => "https://api.openai.com/v1"
        };

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key);

        var body = temperature.HasValue
            ? JsonSerializer.Serialize(new { model, max_tokens = 150,
                                             temperature = (double)temperature.Value,
                                             messages = new[] { new { role = "user", content = prompt } } })
            : JsonSerializer.Serialize(new { model, max_tokens = 150,
                                             messages = new[] { new { role = "user", content = prompt } } });
        using var req  = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"{baseUrl}/chat/completions", req, ct);
        if (!resp.IsSuccessStatusCode) return (null, (int)resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("choices")[0]
                               .GetProperty("message")
                               .GetProperty("content").GetString(), null);
    }

    private static bool IsCloud(string type) => type is not "Ollama";
}
