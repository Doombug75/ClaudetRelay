using System.IO;
using System.Text.Json;

namespace ClaudetRelay.Services;

/// <summary>Configuration for one chat participant slot (P1–P8).</summary>
public class ParticipantConfig
{
    /// <summary>Custom display name shown in chat bubbles. Empty = auto-generated.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// "Ollama" for a local Ollama instance, or a Cloud AI provider name:
    /// "Anthropic", "Google AI", "Groq", "OpenRouter", "Mistral".
    /// </summary>
    public string Type { get; set; } = "Ollama";

    /// <summary>Currently selected model for this participant.</summary>
    public string Model { get; set; } = "";

    /// <summary>Ollama server base URL. Ignored for Cloud AI types.</summary>
    public string ServerUrl { get; set; } = "http://localhost:11434";

    /// <summary>Whether this participant slot is active at startup.</summary>
    public bool Enabled { get; set; } = false;
}

public class AppSettings
{
    // Legacy — kept only for one-time migration to Windows Credential Manager
    public string ClaudeApiKey        { get; set; } = "";

    // Legacy — kept for backward compat and migration source
    public string OllamaBaseUrl       { get; set; } = "http://localhost:11434";
    public string OllamaModel         { get; set; } = "llama3.2";
    public string LastTheme           { get; set; } = "";
    public string SelectedProvider    { get; set; } = "Anthropic";
    public string SelectedCloudModel  { get; set; } = "";
    public bool   CloudAIEnabled      { get; set; } = true;
    public int    OllamaInstanceCount { get; set; } = 1;

    /// <summary>Display name shown on the human user's own chat bubbles.</summary>
    public string UserName { get; set; } = "You";

    /// <summary>Root folder for saved projects. Empty = use default Documents path.</summary>
    public string ProjectsFolder { get; set; } = "";

    /// <summary>
    /// Response tone/style: 0 = strictly neutral, 50 = model default (no injection), 100 = very friendly.
    /// </summary>
    public int ToneLevel { get; set; } = 50;

    /// <summary>Per-participant configuration (P1–P8). Populated on first load via migration.</summary>
    public List<ParticipantConfig> Participants { get; set; } = [];
}

public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Settings", "settings.json");

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        if (!File.Exists(FilePath))
        {
            settings = new AppSettings();
        }
        else
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOpts)
                           ?? new AppSettings();
            }
            catch
            {
                settings = new AppSettings();
            }
        }

        // One-time migration from pre-participant-config settings
        if (settings.Participants.Count == 0)
            MigrateToParticipants(settings);

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, WriteOpts);
            File.WriteAllText(FilePath, json);
        }
        catch { /* silent – missing save should not crash the app */ }
    }

    // ── Migration ──────────────────────────────────────────────────────────

    private static void MigrateToParticipants(AppSettings s)
    {
        s.Participants = [];

        // P1 → first Ollama participant
        s.Participants.Add(new ParticipantConfig
        {
            Name      = "",
            Type      = "Ollama",
            Model     = string.IsNullOrEmpty(s.OllamaModel) ? "llama3.2" : s.OllamaModel,
            ServerUrl = string.IsNullOrEmpty(s.OllamaBaseUrl) ? "http://localhost:11434" : s.OllamaBaseUrl,
            Enabled   = true
        });

        // P2 → Cloud AI participant (use previously configured provider)
        s.Participants.Add(new ParticipantConfig
        {
            Name      = "",
            Type      = string.IsNullOrEmpty(s.SelectedProvider) ? "Anthropic" : s.SelectedProvider,
            Model     = s.SelectedCloudModel ?? "",
            ServerUrl = "http://localhost:11434",
            Enabled   = s.CloudAIEnabled
        });

        // P3–P8 → disabled defaults
        s.Participants.Add(new ParticipantConfig { Type = "Ollama",     Enabled = false });
        s.Participants.Add(new ParticipantConfig { Type = "Anthropic",  Enabled = false });
        s.Participants.Add(new ParticipantConfig { Type = "Groq",       Enabled = false });
        s.Participants.Add(new ParticipantConfig { Type = "Google AI",  Enabled = false });
        s.Participants.Add(new ParticipantConfig { Type = "Mistral",    Enabled = false });
        s.Participants.Add(new ParticipantConfig { Type = "OpenRouter", Enabled = false });
    }
}
