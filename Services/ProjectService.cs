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

    // ── Chat log ──────────────────────────────────────────────────────────

    public static List<ChatLogEntry> LoadChatLog(string projectFolder)
    {
        var path = Path.Combine(projectFolder, "chatlog.json");
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ChatLogEntry>>(
                       File.ReadAllText(path), ReadOpts) ?? [];
        }
        catch { return []; }
    }

    public static void SaveChatLog(string projectFolder, List<ChatLogEntry> log)
    {
        Directory.CreateDirectory(projectFolder);
        File.WriteAllText(
            Path.Combine(projectFolder, "chatlog.json"),
            JsonSerializer.Serialize(log, WriteOpts));
    }

    public static void AppendEntry(string projectFolder, ChatLogEntry entry)
    {
        var log = LoadChatLog(projectFolder);
        log.Add(entry);
        SaveChatLog(projectFolder, log);
    }

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
        Directory.CreateDirectory(Path.Combine(folder, "PROJEKTPLAN"));
        Directory.CreateDirectory(Path.Combine(folder, "OUTPUT"));

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
