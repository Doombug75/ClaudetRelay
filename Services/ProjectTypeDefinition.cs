namespace ClaudetRelay.Services;

/// <summary>
/// Describes a project type loaded from a ProjectTypes/*.xaml ResourceDictionary.
/// All multi-value properties use pipe '|' as separator so they are easy to write in XAML.
/// </summary>
public class ProjectTypeDefinition
{
    // ── Identity ───────────────────────────────────────────────────────────
    public string Name        { get; set; } = "General";
    public string Icon        { get; set; } = "📁";
    public string Description { get; set; } = "";

    // ── Structure ──────────────────────────────────────────────────────────
    /// <summary>Pipe-separated hierarchy levels, e.g. "Chapter|Scene"</summary>
    public string StructureHierarchy { get; set; } = "";

    /// <summary>Pipe-separated icons matching StructureHierarchy, e.g. "📖|🎬"</summary>
    public string StructureIcons { get; set; } = "";

    // ── World-building ─────────────────────────────────────────────────────
    /// <summary>Pipe-separated subfolder names to create inside PROJECTPLAN/, e.g. "Characters|Factions|Locations"</summary>
    public string WorldFolders { get; set; } = "";

    // ── Feature flags ──────────────────────────────────────────────────────
    public bool HasRoadmap        { get; set; } = true;
    public bool HasWorldBuilding  { get; set; } = false;
    public bool HasAssignees      { get; set; } = false;
    public bool HasDeadlines      { get; set; } = false;
    public bool HasPlotNotes      { get; set; } = false;
    public bool HasStageDirections { get; set; } = false;

    // ── AI context hint ────────────────────────────────────────────────────
    /// <summary>Short hint appended to the system prompt for every participant in this project type.</summary>
    public string SystemPromptHint { get; set; } = "";

    // ── Helpers ────────────────────────────────────────────────────────────
    // Accepts both the legacy '|' separator and the current ',' separator.
    private static readonly char[] _sep = ['|', ','];

    public string[] GetStructureLevels() =>
        string.IsNullOrWhiteSpace(StructureHierarchy)
            ? []
            : StructureHierarchy.Split(_sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string[] GetStructureIconList() =>
        string.IsNullOrWhiteSpace(StructureIcons)
            ? []
            : StructureIcons.Split(_sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string[] GetWorldFolderList() =>
        string.IsNullOrWhiteSpace(WorldFolders)
            ? []
            : WorldFolders.Split(_sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override string ToString() => $"{Icon}  {Name}";
}
