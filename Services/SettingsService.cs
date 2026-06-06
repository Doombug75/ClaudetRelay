using System.IO;
using System.Text.Json;

namespace ClaudetRelay.Services;

// ── Bridge / MCP agent model ───────────────────────────────────────────────

public enum BridgeAgentMode { McpServer = 0, ModelController = 1 }

/// <summary>
/// A folder accessible to Bridge agents, with optional write permission.
/// Read access is always granted; write access must be explicitly enabled.
/// </summary>
public class BridgeFolder
{
    public string Id         { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Path       { get; set; } = "";
    public bool   AllowWrite { get; set; } = false;   // read-only by default

    /// <summary>Short display label — last two path segments.</summary>
    public string Label => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One AI model registered as a Bridge agent.
/// Stored in AppSettings and exposed as an MCP tool when the Bridge is running.
/// </summary>
public class BridgeAgent
{
    public string Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Provider    { get; set; } = "Ollama";   // "Ollama" or cloud provider name
    public string Model       { get; set; } = "";
    public string ServerUrl   { get; set; } = "http://localhost:11434";
    public string DisplayName { get; set; } = "";
    public bool   IsEnabled   { get; set; } = true;

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Model : DisplayName;
    public bool   IsLocal => string.Equals(Provider, "Ollama", StringComparison.OrdinalIgnoreCase);
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Rate-limit settings for one cloud API provider.</summary>
public class ProviderThrottleSettings
{
    /// <summary>Whether request throttling is enabled for this provider.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Maximum requests per minute. Must be ≥ 1.</summary>
    public int  Rpm     { get; set; } = 15;
}

/// <summary>Configuration for one chat participant.</summary>
public class ParticipantConfig
{
    /// <summary>Custom display name shown in chat bubbles. Empty = auto-generated.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// "Ollama" for a local Ollama instance, or a Cloud AI provider name:
    /// "Anthropic", "Google AI", "Groq", "OpenRouter", "Mistral", "xAI Grok", "OpenAI ChatGPT".
    /// </summary>
    public string Type { get; set; } = "Ollama";

    /// <summary>Currently selected model for this participant.</summary>
    public string Model { get; set; } = "";

    /// <summary>Ollama server base URL. Ignored for Cloud AI types.</summary>
    public string ServerUrl { get; set; } = "http://localhost:11434";

    /// <summary>Whether this participant is active (participates in conversations).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>UTC timestamp when this participant was created. Used for sort-by-date.</summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    // ── Per-participant rate limiting ──────────────────────────────────────
    // Stored here (not in AppSettings.ProviderThrottle) so each model can have
    // its own budget — e.g. haiku at 60 rpm, opus at 5 rpm on the same account.

    /// <summary>Whether request-rate throttling is active for this participant.</summary>
    public bool RpmEnabled { get; set; } = false;

    /// <summary>Maximum requests per minute for this participant. Must be ≥ 1.</summary>
    public int  Rpm        { get; set; } = 15;

    /// <summary>
    /// Short role label shown on the participant card header (e.g. "Coder", "Analyst").
    /// Set manually or fetched by asking the model itself.
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// One-sentence self-description fetched from the model.
    /// Shown in the model-info dialog instead of the static knowledge-base description.
    /// </summary>
    public string SelfDescription { get; set; } = "";

    /// <summary>What the model says it enjoys most (free-temperature follow-up question).</summary>
    public string Likes { get; set; } = "";

    /// <summary>What the model says it dislikes most (free-temperature follow-up question).</summary>
    public string Dislikes { get; set; } = "";

    /// <summary>
    /// Last HTTP error code from a self-description or API test, e.g. "ERROR:401".
    /// Shown on the participant card instead of "Ready" so the user knows something is wrong.
    /// Cleared on next successful API response.
    /// </summary>
    public string LastApiError { get; set; } = "";

    /// <summary>
    /// Display name of the Windows TTS voice assigned to this participant.
    /// Empty = voice output is silent for this participant.
    /// Populated manually via the voice picker or automatically via "Ask the Model".
    /// </summary>
    public string VoiceName { get; set; } = "";
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
    /// Folder where project ZIP backups are stored.
    /// Empty = backup feature disabled (no backup prompt on project close).
    /// </summary>
    public string BackupFolder { get; set; } = "";

    /// <summary>
    /// Response tone/style: 0 = strictly neutral, 50 = model default (no injection), 100 = very friendly.
    /// </summary>
    public int ToneLevel { get; set; } = 50;

    /// <summary>
    /// When true, all agents adopt a humorous personality.
    /// Slider neutral-end = pure comedy / jokes / poems.
    /// Slider friendly-end = loving pet names, kisses, affectionate insults.
    /// </summary>
    public bool MockingbirdMode { get; set; } = false;

    /// <summary>
    /// Buccaneer mode — all participants respond in full pirate-speak.
    /// Mutually exclusive with MockingbirdMode.
    /// Slider low-end = fierce cutthroat pirate, high-end = jolly friendly cap'n.
    /// </summary>
    public bool BuccaneerMode { get; set; } = false;

    /// <summary>
    /// Global voice output toggle. When false, no TTS speech is produced regardless of
    /// per-participant VoiceName settings. Toggled via the 🔊/🔇 button in the chat toolbar.
    /// </summary>
    public bool VoiceOutputEnabled { get; set; } = false;

    /// <summary>
    /// When true (default), a new AI message immediately stops whatever is currently
    /// playing and speaks the new message. When false, messages queue up and play
    /// one after another — ideal for "tell me a story" multi-response sessions.
    /// </summary>
    public bool VoiceInterruptOnNewMessage { get; set; } = true;

    /// <summary>
    /// Maximum number of characters fed to TTS per message. Longer texts are truncated
    /// with "…" to keep playback snappy. Range 100–5000, default 700.
    /// </summary>
    public int VoiceSpeechMaxChars { get; set; } = 700;

    /// <summary>
    /// UI language code, e.g. "en" or "de".
    /// Applied as <c>CurrentUICulture</c> at startup — restart required for changes.
    /// Empty string means "en" (English / neutral fallback).
    /// </summary>
    public string Language { get; set; } = "";

    /// <summary>Configured participants shown in the card grid.</summary>
    public List<ParticipantConfig> Participants { get; set; } = [];

    /// <summary>Font family used in the chat window and HTML exports. Default "Segoe UI".</summary>
    public string ChatFontFamily { get; set; } = "Segoe UI";

    /// <summary>Font size (pt) used in the chat window and HTML exports. Default 13.</summary>
    public double ChatFontSize { get; set; } = 13.0;

    /// <summary>
    /// Maximum bubble width as a percentage of the chat panel width (1–100).
    /// Default 78 — works well on 1080p; raise on 2K/4K displays.
    /// </summary>
    public double ChatBubbleWidthPercent { get; set; } = 78.0;

    /// <summary>
    /// Per-provider request rate-limit settings.
    /// Key = provider name, e.g. "Google AI", "Groq".
    /// </summary>
    public Dictionary<string, ProviderThrottleSettings> ProviderThrottle { get; set; } = new();

    /// <summary>
    /// When true, AI participants get additional rounds to read and reply to each other
    /// after the first response (multi-round dialogue). Effective in AllRespond mode only.
    /// </summary>
    public bool AiDialogueEnabled { get; set; } = false;

    /// <summary>
    /// Maximum number of turns in an AI-to-AI dialogue session (3–100).
    /// Only used when AiDialogueEnabled is true.
    /// </summary>
    public int AiDialogueMaxTurns { get; set; } = 10;

    /// <summary>
    /// Default response length for general (non-project) chat (0–100).
    /// 50 = model default — no instruction injected.
    /// Project per-participant settings always take priority over this.
    /// </summary>
    public int GlobalResponseLength { get; set; } = 50;

    /// <summary>
    /// How eagerly participants join the conversation unprompted (0–100).
    /// 0 = silent (only respond when directly addressed),
    /// 50 = balanced (respect when someone else is addressed, PASS if nothing new),
    /// 100 = very chatty (always keep the discussion going).
    /// Project settings can override this per project.
    /// </summary>
    public int GlobalChattiness { get; set; } = 50;

    /// <summary>
    /// Port the built-in MCP server listens on. Default 3333.
    /// Change if another service is already using that port.
    /// </summary>
    public int McpPort { get; set; } = 3333;

    /// <summary>
    /// When true the MCP server starts automatically when ClaudetRelay launches.
    /// </summary>
    public bool McpAutoStart { get; set; } = false;

    // ── Bridge / MCP ──────────────────────────────────────────────────────────
    public BridgeAgentMode   BridgeMode               { get; set; } = BridgeAgentMode.McpServer;
    public string            BridgeControllerProvider { get; set; } = "";
    public string            BridgeControllerModel    { get; set; } = "";
    public List<BridgeAgent>  BridgeAgents            { get; set; } = [];
    public List<BridgeFolder> BridgeFolders           { get; set; } = [];

    /// <summary>Tools disabled in MCP Server mode (Claude Desktop / Claude Code connects).</summary>
    public List<string> DisabledMcpServerTools   { get; set; } = [];

    /// <summary>Tools disabled in Model Controller mode (built-in agentic loop).</summary>
    public List<string> DisabledControllerTools  { get; set; } = [];

    /// <summary>Legacy — migrated to the two lists above on first load.</summary>
    public List<string> DisabledBridgeTools { get; set; } = [];

    /// <summary>
    /// Path to the shared temp workspace folder used by parallel agent tasks.
    /// Must be inside a write-enabled Bridge folder. Empty = feature disabled.
    /// </summary>
    public string BridgeTempFolder { get; set; } = "";

    // ── MCP Server limits (external client connects — always cloud) ──────────
    /// <summary>Max bytes for bridge_read_file in MCP Server mode. Default 200 KB.</summary>
    public int McpServerMaxTextFileBytes   { get; set; } = 200_000;

    /// <summary>Max bytes for bridge_read_file_binary in MCP Server mode. Default 10 MB.</summary>
    public int McpServerMaxBinaryFileBytes { get; set; } = 10_000_000;

    // ── Model Controller limits (local Ollama or cloud API controller) ────────
    /// <summary>Max bytes for bridge_read_file when Controller uses a local Ollama model. Default 1 MB.</summary>
    public int BridgeLocalMaxTextFileBytes   { get; set; } = 1_000_000;

    /// <summary>Max bytes for bridge_read_file when Controller uses a cloud API model. Default 200 KB.</summary>
    public int BridgeCloudMaxTextFileBytes   { get; set; } = 200_000;

    /// <summary>Max bytes for bridge_read_file_binary when Controller uses a local Ollama model. Default 50 MB.</summary>
    public int BridgeLocalMaxBinaryFileBytes { get; set; } = 50_000_000;

    /// <summary>Max bytes for bridge_read_file_binary when Controller uses a cloud API model. Default 10 MB.</summary>
    public int BridgeCloudMaxBinaryFileBytes { get; set; } = 10_000_000;

    /// <summary>Font size for the Controller chat output panel. Default 12.</summary>
    public double BridgeControllerFontSize { get; set; } = 12;

    /// <summary>
    /// UI zoom factor applied to all windows (0.5 = 50 %, 1.0 = 100 %, 3.0 = 300 %).
    /// Default 1.0. Changed in General Settings.
    /// </summary>
    public double UiZoom { get; set; } = 1.0;

    /// <summary>
    /// Optional path to an external world-editor executable.
    /// When set and the file exists, the 🌍 World button launches this program
    /// instead of opening the built-in world editor.
    /// The project folder is passed as the first command-line argument so the
    /// external tool can locate PROJECTPLAN/{EntityType}/*.json directly.
    /// Leave empty to use the built-in editor.
    /// </summary>
    public string ExternalWorldEditorPath { get; set; } = "";
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


        // One-time migration: copy ProviderThrottle → per-participant RpmEnabled/Rpm.
        // Runs once when ProviderThrottle still has data but no participant has RpmEnabled set.
        if (settings.ProviderThrottle.Count > 0 &&
            settings.Participants.All(p => !p.RpmEnabled))
        {
            foreach (var p in settings.Participants)
            {
                if (settings.ProviderThrottle.TryGetValue(p.Type, out var t) && t.Enabled)
                {
                    p.RpmEnabled = true;
                    p.Rpm        = t.Rpm;
                }
            }
            settings.ProviderThrottle.Clear();   // no longer needed
            Save(settings);
        }

        // Migrate legacy single DisabledBridgeTools list to per-mode lists
        if (settings.DisabledBridgeTools.Count > 0 &&
            settings.DisabledMcpServerTools.Count == 0 &&
            settings.DisabledControllerTools.Count == 0)
        {
            settings.DisabledMcpServerTools  = [..settings.DisabledBridgeTools];
            settings.DisabledControllerTools = [..settings.DisabledBridgeTools];
            settings.DisabledBridgeTools.Clear();
            Save(settings);
        }

        // Default temp workspace — Documents\ClaudetRelay\Workspace
        if (string.IsNullOrWhiteSpace(settings.BridgeTempFolder))
        {
            settings.BridgeTempFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ClaudetRelay", "Workspace");
            Save(settings);   // persist so the user sees it in the UI on first open
        }

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

}
