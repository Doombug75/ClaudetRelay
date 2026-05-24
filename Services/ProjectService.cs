using System.IO;
using System.Text.Json;

namespace ClaudetRelay.Services;

// ── Project data models ────────────────────────────────────────────────────

public class ProjectParticipant
{
    public string Type        { get; set; } = "Ollama";   // "Ollama" | provider name
    public string Provider    { get; set; } = "";
    public string ModelName   { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool   IsActive    { get; set; } = true;
}

public class ProjectMeta
{
    public string   ProjectName { get; set; } = "Untitled";
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime LastOpened  { get; set; } = DateTime.UtcNow;
    public List<ProjectParticipant> Participants { get; set; } = [];
}

// ── Project settings (roles, orchestration) ────────────────────────────────

/// <summary>How participants interact when a user message arrives.</summary>
public enum OrchestrationMode
{
    /// <summary>All active participants respond to every message (default behaviour).</summary>
    AllRespond = 0,

    /// <summary>
    /// The Coordinator responds first and may delegate to one or more Reasoners
    /// by tagging them (e.g. @Reasoner) in its reply.
    /// </summary>
    CoordinatorFirst = 1,

    /// <summary>
    /// All participants respond; the Coordinator then receives all answers as context
    /// and writes a final synthesising summary.
    /// </summary>
    CoordinatorSummarizes = 2
}

/// <summary>Role assignment for one participant within a specific project.</summary>
public class ProjectParticipantRole
{
    /// <summary>Provider: "Ollama", "Anthropic", "Google AI", etc. Used as lookup key.</summary>
    public string Provider         { get; set; } = "";

    /// <summary>Model name. Used as lookup key together with Provider.</summary>
    public string Model            { get; set; } = "";

    /// <summary>Human-readable name shown in the UI. Not used for lookup.</summary>
    public string DisplayName      { get; set; } = "";

    /// <summary>
    /// Coordinator receives every user message first and decides routing.
    /// Only one coordinator should be active per project.
    /// </summary>
    public bool IsCoordinator      { get; set; } = false;

    /// <summary>
    /// Reasoner executes tasks delegated by the coordinator.
    /// Multiple reasoners are allowed; higher priority = preferred first.
    /// </summary>
    public bool IsReasoner         { get; set; } = false;

    /// <summary>Task priority 1 (lowest) – 10 (highest). Meaningful only when IsReasoner = true.</summary>
    public int  ReasonerPriority   { get; set; } = 5;

    /// <summary>
    /// Character name this participant answers as in this project.
    /// Empty = use default display name.
    /// </summary>
    public string AnswerAsName     { get; set; } = "";

    /// <summary>
    /// Custom role / character instruction injected into the system prompt for this
    /// participant in this project. Empty = no custom role.
    /// </summary>
    public string RoleInstruction  { get; set; } = "";

    /// <summary>
    /// Response length hint: 0 = one-liner, 50 = model default (no injection), 100 = monologue.
    /// </summary>
    public int ResponseLength      { get; set; } = 50;

    /// <summary>
    /// Whether this participant is active in the current project / scene.
    /// Inactive participants are skipped during AI response rounds.
    /// </summary>
    public bool IsActive           { get; set; } = true;
}

/// <summary>Per-project settings saved as <c>project-settings.json</c> inside the project folder.</summary>
public class ProjectSettings
{
    public OrchestrationMode            OrchestrationMode { get; set; } = OrchestrationMode.AllRespond;
    public List<ProjectParticipantRole> Roles             { get; set; } = [];

    /// <summary>
    /// Language all AI participants must use in this project.
    /// Empty string = follow the conversation language (model default).
    /// </summary>
    public string Language { get; set; } = "";

    /// <summary>
    /// Maximum number of consecutive AI-to-AI response rounds after a user message.
    /// 1 = only respond to user (no chaining). Default = 3.
    /// </summary>
    public int MaxDialogDepth { get; set; } = 3;

    /// <summary>
    /// Default response length (0–100) applied to new participants when they are first
    /// added to this project. Existing roles are not changed unless "Apply to All" is used.
    /// 50 = model default (no injection).
    /// </summary>
    public int DefaultResponseLength { get; set; } = 50;

    /// <summary>Looks up the role for a participant by provider + model (case-insensitive).
    /// Creates and registers a new empty role if none exists, using
    /// <see cref="DefaultResponseLength"/> as the initial ResponseLength.</summary>
    public ProjectParticipantRole GetOrCreate(string provider, string model, string displayName)
    {
        var existing = Roles.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Model,    model,    StringComparison.OrdinalIgnoreCase));

        if (existing is not null) return existing;

        var newRole = new ProjectParticipantRole
            { Provider = provider, Model = model, DisplayName = displayName,
              ResponseLength = DefaultResponseLength };
        Roles.Add(newRole);
        return newRole;
    }

    /// <summary>Returns the role for a participant, or null if none is saved.</summary>
    public ProjectParticipantRole? Get(string provider, string model) =>
        Roles.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Model,    model,    StringComparison.OrdinalIgnoreCase));
}

// ── Character file (portable character definition) ─────────────────────────

/// <summary>
/// Portable character definition saved as a .json file in the project's
/// Characters folder.  Lets authors build a library of characters that can
/// be loaded into any participant slot.
/// </summary>
public class CharacterData
{
    public string AnswerAsName    { get; set; } = "";
    public string RoleInstruction { get; set; } = "";
    public int    ResponseLength  { get; set; } = 50;
}

public class ChatLogEntry
{
    public DateTime Timestamp   { get; set; } = DateTime.Now;
    /// <summary>"User", "AI", or "System"</summary>
    public string   SenderType  { get; set; } = "";
    public string   Provider    { get; set; } = "";
    public string   ModelName   { get; set; } = "";
    public string   DisplayName { get; set; } = "";
    public string   AvatarLabel { get; set; } = "";
    public string   AccentKey   { get; set; } = "";
    public string   BubbleKey   { get; set; } = "";
    public bool     IsUser      { get; set; } = false;
    public string   Message     { get; set; } = "";
}

// ── Project service ────────────────────────────────────────────────────────

public static class ProjectService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    // ── Folder resolution ──────────────────────────────────────────────────

    public static string GetDefaultProjectsFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ClaudetRelay", "Projects");

    /// <summary>Returns configured folder or the default if empty.</summary>
    public static string ResolveFolder(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? GetDefaultProjectsFolder() : configured;

    // ── List ──────────────────────────────────────────────────────────────

    public static List<(string Folder, ProjectMeta Meta)> ListProjects(string folder)
    {
        var result = new List<(string, ProjectMeta)>();
        if (!Directory.Exists(folder)) return result;

        foreach (var dir in Directory.GetDirectories(folder)
                                     .OrderByDescending(Directory.GetLastWriteTime))
        {
            var meta = LoadMeta(dir);
            if (meta is not null) result.Add((dir, meta));
        }
        return result;
    }

    // ── Load / Save meta ──────────────────────────────────────────────────

    public static ProjectMeta? LoadMeta(string projectFolder)
    {
        var path = Path.Combine(projectFolder, "project.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProjectMeta>(File.ReadAllText(path), ReadOpts);
        }
        catch { return null; }
    }

    public static void SaveMeta(string projectFolder, ProjectMeta meta)
    {
        Directory.CreateDirectory(projectFolder);
        File.WriteAllText(
            Path.Combine(projectFolder, "project.json"),
            JsonSerializer.Serialize(meta, WriteOpts));
    }

    // ── Project settings ──────────────────────────────────────────────────

    public static ProjectSettings LoadProjectSettings(string projectFolder)
    {
        var path = Path.Combine(projectFolder, "project-settings.json");
        if (!File.Exists(path)) return new ProjectSettings();
        try
        {
            return JsonSerializer.Deserialize<ProjectSettings>(
                       File.ReadAllText(path), ReadOpts) ?? new ProjectSettings();
        }
        catch { return new ProjectSettings(); }
    }

    public static void SaveProjectSettings(string projectFolder, ProjectSettings settings)
    {
        Directory.CreateDirectory(projectFolder);
        File.WriteAllText(
            Path.Combine(projectFolder, "project-settings.json"),
            JsonSerializer.Serialize(settings, WriteOpts));
    }

    // ── Chat log ──────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum number of entries per chatlog segment file.
    /// When chatlog.json reaches this count it is rotated to
    /// chatlog-yyyy-MM-dd_HHmmss.json and a fresh chatlog.json is started.
    /// </summary>
    public const int ChatLogMaxEntries = 500;

    /// <summary>
    /// Returns all chatlog segment files in chronological order:
    /// chatlog-yyyy-MM-dd_HHmmss.json (archived, oldest first), chatlog.json (current).
    /// ISO-format filenames sort lexicographically = chronologically.
    /// </summary>
    private static IEnumerable<string> GetChatLogFiles(string projectFolder)
    {
        var archived = Directory.Exists(projectFolder)
            ? Directory.GetFiles(projectFolder, "chatlog-*.json")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            : Enumerable.Empty<string>();

        var current = Path.Combine(projectFolder, "chatlog.json");
        return File.Exists(current) ? archived.Append(current) : archived;
    }

    /// <summary>Loads all chatlog segments and returns them as one flat list.</summary>
    public static List<ChatLogEntry> LoadChatLog(string projectFolder)
    {
        var result = new List<ChatLogEntry>();
        foreach (var file in GetChatLogFiles(projectFolder))
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<ChatLogEntry>>(
                                  File.ReadAllText(file), ReadOpts);
                if (entries is not null) result.AddRange(entries);
            }
            catch { /* skip corrupt segment */ }
        }
        return result;
    }

    /// <summary>
    /// Writes <paramref name="log"/> to chatlog.json (current segment only).
    /// Does NOT rotate — use <see cref="AppendEntry"/> for normal message flow.
    /// </summary>
    public static void SaveChatLog(string projectFolder, List<ChatLogEntry> log)
    {
        Directory.CreateDirectory(projectFolder);
        File.WriteAllText(
            Path.Combine(projectFolder, "chatlog.json"),
            JsonSerializer.Serialize(log, WriteOpts));
    }

    /// <summary>
    /// Appends one entry to the current chatlog segment.
    /// If the segment has reached <see cref="ChatLogMaxEntries"/> it is first
    /// rotated to chatlog-N.json and a fresh chatlog.json is started.
    /// </summary>
    public static void AppendEntry(string projectFolder, ChatLogEntry entry)
    {
        Directory.CreateDirectory(projectFolder);
        var currentPath = Path.Combine(projectFolder, "chatlog.json");

        // Load only the current segment (not all history)
        List<ChatLogEntry> current = [];
        if (File.Exists(currentPath))
        {
            try
            {
                current = JsonSerializer.Deserialize<List<ChatLogEntry>>(
                              File.ReadAllText(currentPath), ReadOpts) ?? [];
            }
            catch { current = []; }
        }

        // Rotate when full — stamp with the current local time so the filename
        // is both human-readable and lexicographically chronological.
        if (current.Count >= ChatLogMaxEntries)
        {
            var stamp    = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var archPath = Path.Combine(projectFolder, $"chatlog-{stamp}.json");
            // Guard against a collision if two rotations happen within the same second
            if (File.Exists(archPath))
                archPath = Path.Combine(projectFolder, $"chatlog-{stamp}-1.json");
            File.Move(currentPath, archPath);
            current = [];
        }

        current.Add(entry);
        File.WriteAllText(currentPath, JsonSerializer.Serialize(current, WriteOpts));
    }

    /// <summary>Returns the next available chatlog archive index (1-based).</summary>
    // ── Create / Delete ────────────────────────────────────────────────────

    /// <summary>Creates a new project subfolder and writes project.json. Returns the folder path.</summary>
    public static string CreateProject(string parentFolder, string name)
    {
        Directory.CreateDirectory(parentFolder);
        var safeName = MakeSafeName(name);
        var folder   = Path.Combine(parentFolder, safeName);
        int i = 1;
        while (Directory.Exists(folder))
            folder = Path.Combine(parentFolder, $"{safeName}_{i++}");

        Directory.CreateDirectory(folder);

        // Standard project subfolders
        Directory.CreateDirectory(Path.Combine(folder, "INPUT"));
        Directory.CreateDirectory(Path.Combine(folder, "PROJECTPLAN"));
        Directory.CreateDirectory(Path.Combine(folder, "OUTPUT"));
        Directory.CreateDirectory(Path.Combine(folder, "AI-Characters"));

        SaveMeta(folder, new ProjectMeta
        {
            ProjectName = name,
            CreatedAt   = DateTime.UtcNow,
            LastOpened  = DateTime.UtcNow
        });
        return folder;
    }

    // ── Sandboxing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true only if <paramref name="path"/> resolves to a location
    /// inside <paramref name="projectFolder"/>. Use this to validate every
    /// file-system path that originates from AI output or user input, so that
    /// a rogue model or manipulated server cannot escape the project sandbox.
    /// </summary>
    public static bool IsPathSafe(string path, string projectFolder)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(projectFolder).TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Safe file-write helper: only writes if the resolved path stays inside
    /// <paramref name="projectFolder"/>. Returns false and writes nothing otherwise.
    /// </summary>
    public static bool SafeWriteFile(string projectFolder, string relativePath, string content)
    {
        var full = Path.GetFullPath(Path.Combine(projectFolder, relativePath));
        if (!IsPathSafe(full, projectFolder)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return true;
    }

    /// <summary>
    /// Safe file-read helper: only reads if the resolved path stays inside
    /// <paramref name="projectFolder"/>. Returns null otherwise.
    /// </summary>
    public static string? SafeReadFile(string projectFolder, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(projectFolder, relativePath));
        if (!IsPathSafe(full, projectFolder)) return null;
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    /// <summary>Lists all files in INPUT sub-folder (sandboxed).</summary>
    public static List<string> ListInputFiles(string projectFolder)
    {
        var input = Path.Combine(projectFolder, "INPUT");
        if (!Directory.Exists(input)) return [];
        return Directory.GetFiles(input).Select(Path.GetFileName).ToList()!;
    }

    // ── AI-Characters folder ───────────────────────────────────────────────

    public static string GetCharactersFolder(string projectFolder) =>
        Path.Combine(projectFolder, "AI-Characters");

    public static List<string> ListCharacterFiles(string projectFolder)
    {
        var folder = GetCharactersFolder(projectFolder);
        if (!Directory.Exists(folder)) return [];
        return Directory.GetFiles(folder, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => n is not null)
                        .ToList()!;
    }

    public static void SaveCharacterFile(string projectFolder, string name, CharacterData data)
    {
        var folder = GetCharactersFolder(projectFolder);
        Directory.CreateDirectory(folder);
        var safe = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (string.IsNullOrEmpty(safe)) safe = "Character";
        File.WriteAllText(Path.Combine(folder, safe + ".json"),
                          JsonSerializer.Serialize(data, WriteOpts));
    }

    public static CharacterData? LoadCharacterFile(string projectFolder, string name)
    {
        var path = Path.Combine(GetCharactersFolder(projectFolder), name + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CharacterData>(File.ReadAllText(path), ReadOpts); }
        catch { return null; }
    }

    public static void DeleteProject(string projectFolder)
    {
        if (Directory.Exists(projectFolder))
            Directory.Delete(projectFolder, recursive: true);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string MakeSafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe    = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(safe) ? "Project" : safe;
    }
}
