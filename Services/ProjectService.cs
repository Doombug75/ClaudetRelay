using System.IO;
using System.Text.Json;

namespace ClaudetRelay.Services;

// ── Project data model ────────────────────────────────────────────────────
// Single class — identity + settings stored together in PROJECTSETTINGS/project.json.
// No separate ProjectMeta / project-settings.json split.

/// <summary>How participants interact when a user message arrives.</summary>
public enum OrchestrationMode
{
    /// <summary>
    /// Full Manual Mode — all active participants respond to every message.
    /// No coordinator automation: no SuperPowers calibration, no work-session greeting.
    /// The user manages all task assignments manually.
    /// </summary>
    AllRespond = 0,

    /// <summary>
    /// The Coordinator responds first and may delegate to one or more Reasoners
    /// by tagging them (e.g. @Reasoner) in its reply. Default mode.
    /// </summary>
    CoordinatorFirst = 1,

    /// <summary>
    /// All participants respond; the Coordinator then receives all answers as context
    /// and writes a final synthesising summary.
    /// </summary>
    CoordinatorSummarizes = 2,

    /// <summary>
    /// Legacy — kept for backward compatibility with saved projects.
    /// Treated identically to <see cref="CoordinatorFirst"/> at runtime.
    /// </summary>
    CoordinatorAuto   = 3,

    /// <summary>
    /// The user communicates only with the Coordinator. All AI-to-AI work (Coordinator
    /// deliberation and Reasoner responses) runs hidden from the user's chat. Small status
    /// indicators show which participant is active. Only the Coordinator's final synthesis
    /// is shown to the user.
    /// </summary>
    CoordinatorOnly   = 4,
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
    /// Critic reviews other participants' output for consistency, logic errors,
    /// and factual accuracy. Multiple critics allowed.
    /// </summary>
    public bool IsCritic           { get; set; } = false;

    /// <summary>
    /// Planner breaks down the user's goal into a structured work plan before
    /// execution begins. Multiple planners allowed.
    /// </summary>
    public bool IsPlanner          { get; set; } = false;

    /// <summary>
    /// Researcher gathers information, looks up references, or explores context
    /// before the main answer is produced. Multiple researchers allowed.
    /// </summary>
    public bool IsResearcher       { get; set; } = false;

    /// <summary>
    /// Write Access — participant may use &lt;output&gt; and &lt;projectplan&gt; file-write tags.
    /// Coordinators always have write access regardless of this flag.
    /// All other participants are read-only unless this flag is set.
    /// </summary>
    public bool IsWriteAccess      { get; set; } = false;

    /// <summary>
    /// Whether this participant is active in the current project / scene.
    /// Inactive participants are skipped during AI response rounds.
    /// </summary>
    public bool IsActive           { get; set; } = true;
}

/// <summary>
/// All project data in one place — identity, settings, and roles.
/// Saved as <c>PROJECTSETTINGS/project.json</c> inside the project folder.
/// </summary>
public class ProjectSettings
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string   ProjectName     { get; set; } = "Untitled";
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime LastOpened      { get; set; } = DateTime.UtcNow;

    /// <summary>Matches ProjectTypeDefinition.Name (e.g. "Novel", "Software Project"). Empty = General.</summary>
    public string   ProjectTypeName { get; set; } = "General";

    /// <summary>Freeform project description injected into every AI system prompt.</summary>
    public string   Description     { get; set; } = "";

    // ── Orchestration ──────────────────────────────────────────────────────
    public OrchestrationMode            OrchestrationMode { get; set; } = OrchestrationMode.CoordinatorFirst;
    public List<ProjectParticipantRole> Roles             { get; set; } = [];

    /// <summary>Language override. Empty = follow conversation language.</summary>
    public string Language { get; set; } = "";

    /// <summary>Max consecutive AI-to-AI response rounds per user message.</summary>
    public int MaxDialogDepth { get; set; } = 3;

    /// <summary>Default response length (0–100) for new participants. 50 = model default.</summary>
    public int DefaultResponseLength { get; set; } = 50;

    /// <summary>
    /// How eagerly participants join the conversation unprompted (0–100).
    /// Overrides the global General Settings value for this project.
    /// -1 = use global setting.
    /// </summary>
    public int DefaultChattiness { get; set; } = -1;

    /// <summary>
    /// When true, MCP clients (Claude Desktop, Claude Code) can read and post to this
    /// project's chat via the chat_get_history and chat_post_message MCP tools.
    /// Enable by adding an MCP Client via the + participant menu while the project is open.
    /// </summary>
    public bool McpChatEnabled { get; set; } = false;

    /// <summary>Legacy — no longer used. Kept so existing JSON round-trips cleanly.</summary>
    public string TeamPlan { get; set; } = "";

    /// <summary>
    /// True once the coordinator has started the roadmap-building conversation.
    /// Reset to false to restart from Project Settings.
    /// </summary>
    public bool RoadmapInitialized { get; set; } = false;

    /// <summary>
    /// Participants active the last time this project was open, used for positional role matching.
    /// </summary>
    public List<ParticipantConfig>? ActiveParticipants { get; set; } = null;

    /// <summary>
    /// Bridge agent roster saved for this project.
    /// When non-null and non-empty, the Bridge panel offers to use these agents instead of the
    /// global roster. The global roster is restored automatically when the project is closed.
    /// </summary>
    public List<BridgeAgent>? SavedBridgeAgents { get; set; } = null;

    /// <summary>
    /// How much creative autonomy the AI has in this project (0–4).
    ///   0 = Assistant  — only acts on explicit instructions, never writes files without approval
    ///   1 = Cooperative — brainstorms and plans; every decision stays with the user  (default)
    ///   2 = Directed Creativity — plans first, asks for go-ahead, then works in roadmap order
    ///   3 = Creative — uses roadmap/INPUT/world data; asks once to start, then creates in order
    ///   4 = Creativity Chaos — maximum autonomy; creative but still logically coherent
    /// </summary>
    public int AutonomyMode { get; set; } = 1;

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

    /// <summary>Path to the single project file inside a project folder.</summary>
    public static string ProjectFilePath(string projectFolder) =>
        Path.Combine(projectFolder, "PROJECTSETTINGS", "project.json");

    // ── List ──────────────────────────────────────────────────────────────

    public static List<(string Folder, ProjectSettings Settings)> ListProjects(string folder)
    {
        var result = new List<(string, ProjectSettings)>();
        if (!Directory.Exists(folder)) return result;

        foreach (var dir in Directory.GetDirectories(folder)
                                     .OrderByDescending(Directory.GetLastWriteTime))
        {
            var ps = LoadProject(dir);
            if (ps is not null)
                result.Add((dir, ps));
            // Silently skip projects that fail to load (corrupted, missing fields, etc.)
        }
        return result;
    }

    // ── Load / Save ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads the project from <c>PROJECTSETTINGS/project.json</c>.
    /// Returns <c>null</c> when the file is absent (not a project folder).
    /// </summary>
    public static ProjectSettings? LoadProject(string projectFolder)
    {
        var path = ProjectFilePath(projectFolder);
        if (!File.Exists(path))
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"🔍 [LoadProject] No project.json at: {path}");
#endif
            return null;
        }
        try
        {
            var content = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<ProjectSettings>(content, ReadOpts);
#if DEBUG
            if (result is null)
                System.Diagnostics.Debug.WriteLine($"🔍 [LoadProject] Deserialization returned null for: {Path.GetFileName(projectFolder)}");
            else
                System.Diagnostics.Debug.WriteLine($"✓ [LoadProject] Loaded: {result.ProjectName}");
#endif
            return result;
        }
        catch (Exception
#if DEBUG
            ex
#endif
            )
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"✗ [LoadProject] ERROR loading {Path.GetFileName(projectFolder)}: {ex.Message}");
#endif
            return null;
        }
    }

    /// <summary>
    /// Saves all project data to <c>PROJECTSETTINGS/project.json</c>.
    /// Creates the PROJECTSETTINGS folder if needed.
    /// </summary>
    public static void SaveProject(string projectFolder, ProjectSettings settings)
    {
        var dir = Path.GetDirectoryName(ProjectFilePath(projectFolder))!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ProjectFilePath(projectFolder),
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

    /// <summary>
    /// Creates a new project subfolder and writes project.json. Returns the folder path.
    /// <paramref name="typeName"/> is stored in the meta and shown in the UI.
    /// <paramref name="worldFolders"/> are created inside PROJECTPLAN/ (e.g. Characters, Factions).
    /// </summary>
    public static string CreateProject(string parentFolder, string name,
                                       string typeName      = "General",
                                       string[]? worldFolders = null)
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
        var planFolder = Path.Combine(folder, "PROJECTPLAN");
        Directory.CreateDirectory(planFolder);
        Directory.CreateDirectory(Path.Combine(folder, "OUTPUT"));
        Directory.CreateDirectory(Path.Combine(folder, "AI-Characters"));
        Directory.CreateDirectory(Path.Combine(folder, "PROJECTSETTINGS"));

        // Type-specific world-building subfolders inside PROJECTPLAN/
        if (worldFolders is { Length: > 0 })
        {
            foreach (var wf in worldFolders)
            {
                var safe = new string(wf.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
                if (!string.IsNullOrEmpty(safe))
                    Directory.CreateDirectory(Path.Combine(planFolder, safe));
            }
        }

        SaveProject(folder, new ProjectSettings
        {
            ProjectName     = name,
            ProjectTypeName = typeName,
            CreatedAt       = DateTime.UtcNow,
            LastOpened      = DateTime.UtcNow
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
    /// <paramref name="directoryCreated"/> is true when the containing directory
    /// did not exist and was freshly created — callers can notify the coordinator.
    /// </summary>
    public static bool SafeWriteFile(string projectFolder, string relativePath, string content,
                                     out bool directoryCreated)
    {
        directoryCreated = false;
        var full = Path.GetFullPath(Path.Combine(projectFolder, relativePath));
        if (!IsPathSafe(full, projectFolder)) return false;
        var dir = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            directoryCreated = true;
        }
        File.WriteAllText(full, content);
        return true;
    }

    /// <inheritdoc cref="SafeWriteFile(string,string,string,out bool)"/>
    public static bool SafeWriteFile(string projectFolder, string relativePath, string content)
        => SafeWriteFile(projectFolder, relativePath, content, out _);

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

    /// <summary>
    /// Returns true if a project folder derived from <paramref name="name"/> already exists
    /// inside <paramref name="parentFolder"/>. Uses the same name-sanitisation as
    /// <see cref="CreateProject"/> so the check is always consistent.
    /// </summary>
    public static bool ProjectNameExists(string parentFolder, string name) =>
        Directory.Exists(Path.Combine(parentFolder, MakeSafeName(name)));
}
