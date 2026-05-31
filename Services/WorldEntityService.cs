using System.IO;
using System.Text.Json;

namespace ClaudetRelay.Services;

// ── World entity model ─────────────────────────────────────────────────────

/// <summary>
/// One world-building entry (character, location, faction, lore element, etc.).
/// Stored as a single JSON file inside PROJECTPLAN/{EntityType}/.
/// </summary>
public class WorldEntity
{
    public string   Id         { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string   Name       { get; set; } = "";
    public string   EntityType { get; set; } = "";
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;

    /// <summary>Schema-driven property bag. Keys match WorldEntitySchemas entries.</summary>
    public Dictionary<string, string> Fields { get; set; } = new();

    /// <summary>Freeform notes not covered by the schema.</summary>
    public string Notes { get; set; } = "";

    // ── Faction-specific ──────────────────────────────────────────────────

    /// <summary>
    /// Hex colour string for the faction dot badge (e.g. "#E53935"). Faction entities only.
    /// Empty = no colour assigned yet.
    /// </summary>
    public string FactionColor { get; set; } = "";

    /// <summary>
    /// IDs of member entities (Characters etc.) that belong to this faction.
    /// Faction entities only.
    /// </summary>
    public List<string> MemberIds { get; set; } = [];
}

// ── Field schemas ──────────────────────────────────────────────────────────

public static class WorldEntitySchemas
{
    /// <summary>
    /// Maps entity type name → ordered list of (FieldName, PlaceholderHint) pairs.
    /// </summary>
    public static readonly Dictionary<string, List<(string Field, string Hint)>> All = new()
    {
        ["Character"] =
        [
            ("Role",              "protagonist / antagonist / supporting / NPC"),
            ("Age",               ""),
            ("Level / Classes",   "Fighter 5 / Wizard 3 / Rogue 1 …"),
            ("Alignment",         "Lawful Good / Chaotic Neutral / True Neutral …"),
            ("Background",        "Origins and history"),
            ("Goal",              "What do they want most?"),
            ("Flaw",              "What holds them back or haunts them?"),
            ("Arc",               "How do they change over the story?"),
            ("Voice",             "How do they speak? Any mannerisms?"),
            ("Health / Resources","HP · AC · Mana · Stamina · Luck …"),
            ("Attributes",        "STR 16  DEX 12  CON 14  INT 10  WIS 13  CHA 8"),
            ("Skills",            "Stealth +7  Persuasion +5  Arcana +3 …")
        ],
        ["Location"] =
        [
            ("Type",             "city / building / wilderness / imagined …"),
            ("Description",      "Visual and sensory details"),
            ("Atmosphere",       "Mood and feeling of this place"),
            ("Significance",     "Why does this place matter to the story?"),
            ("First appears in", "Chapter or scene reference")
        ],
        ["Faction"] =
        [
            ("Type",      "political / religious / criminal / military …"),
            ("Goal",      "What does this group want?"),
            ("Leader",    "Who leads them?"),
            ("Territory", "Where do they operate?"),
            ("Alignment", "ally / enemy / neutral / unknown to protagonist")
        ],
        ["Lore"] =
        [
            ("Category",    "history / myth / magic / prophecy / technology …"),
            ("Description", "What is this lore entry?"),
            ("Known by",    "Which characters are aware of this?"),
            ("Source",      "Where does this knowledge come from?")
        ]
    };

    /// <summary>Returns the schema for the given entity type, or an empty list if unknown.</summary>
    public static List<(string Field, string Hint)> For(string entityType) =>
        All.TryGetValue(entityType, out var schema) ? schema : [];

    /// <summary>
    /// Palette of 15 distinct hex colours for faction dot badges.
    /// More than the four accent brushes so each faction stays visually unique.
    /// </summary>
    public static readonly string[] FactionColorPalette =
    [
        "#E53935", // Red
        "#E91E63", // Pink
        "#9C27B0", // Purple
        "#673AB7", // Deep Purple
        "#3F51B5", // Indigo
        "#2196F3", // Blue
        "#03A9F4", // Light Blue
        "#00BCD4", // Cyan
        "#009688", // Teal
        "#4CAF50", // Green
        "#FF9800", // Orange
        "#FF5722", // Deep Orange
        "#795548", // Brown
        "#607D8B", // Blue Grey
        "#757575", // Grey
    ];
}

// ── Service ────────────────────────────────────────────────────────────────

public static class WorldEntityService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    // ── Paths ──────────────────────────────────────────────────────────────

    public static string GetEntityFolder(string projFolder, string entityType)
    {
        // Always use the singular form as the canonical folder name
        // ("Characters" → "Character", "Factions" → "Faction", "Lore" → "Lore")
        var folderName = entityType.TrimEnd('s');
        if (string.IsNullOrEmpty(folderName)) folderName = entityType;

        var canonicalPath = Path.Combine(projFolder, "PROJECTPLAN", folderName);

        // One-time silent migration: rename plural → singular if needed
        var legacyPath = Path.Combine(projFolder, "PROJECTPLAN", entityType);
        if (!string.Equals(legacyPath, canonicalPath, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(canonicalPath)
            && Directory.Exists(legacyPath))
        {
            try { Directory.Move(legacyPath, canonicalPath); }
            catch { /* best-effort */ }
        }

        return canonicalPath;
    }

    private static string EntityFilePath(string projFolder, WorldEntity entity) =>
        Path.Combine(GetEntityFolder(projFolder, entity.EntityType),
                     MakeSafeName(entity.Name) + ".json");

    // ── CRUD ───────────────────────────────────────────────────────────────

    /// <summary>Saves (creates or overwrites) the entity file. Updates UpdatedAt.</summary>
    public static void Save(string projFolder, WorldEntity entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        var dir = GetEntityFolder(projFolder, entity.EntityType);
        Directory.CreateDirectory(dir);
        File.WriteAllText(EntityFilePath(projFolder, entity),
                          JsonSerializer.Serialize(entity, WriteOpts));
    }

    /// <summary>Renames an entity: deletes the old file and writes a new one.</summary>
    public static void Rename(string projFolder, WorldEntity entity, string oldName)
    {
        var oldPath = Path.Combine(GetEntityFolder(projFolder, entity.EntityType),
                                   MakeSafeName(oldName) + ".json");
        if (File.Exists(oldPath)) File.Delete(oldPath);
        Save(projFolder, entity);
    }

    /// <summary>Returns all entities of the given type, sorted by name.</summary>
    public static List<WorldEntity> List(string projFolder, string entityType)
    {
        var dir = GetEntityFolder(projFolder, entityType);
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "*.json")
            .Select(f =>
            {
                try   { return JsonSerializer.Deserialize<WorldEntity>(File.ReadAllText(f), ReadOpts); }
                catch { return null; }
            })
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Name))
            .OrderBy(e => e!.Name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    /// <summary>Deletes the entity file. Silent if file does not exist.</summary>
    public static void Delete(string projFolder, WorldEntity entity)
    {
        var path = EntityFilePath(projFolder, entity);
        if (File.Exists(path)) File.Delete(path);
    }

    // ── Helper ─────────────────────────────────────────────────────────────

    public static string MakeSafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe    = new string(name.Where(c => !invalid.Contains(c)).ToArray())
                          .Trim().Replace(' ', '_');
        return string.IsNullOrEmpty(safe) ? "entity" : safe;
    }
}

// ── Entity board layout & relations ───────────────────────────────────────

/// <summary>Stored canvas position of one entity card on the board view.</summary>
public class BoardPosition
{
    public double X { get; set; } = 60;
    public double Y { get; set; } = 60;
}

/// <summary>Visual style of a relation line on the board.</summary>
public enum BoardLineStyle
{
    Solid, Dotted, Dashed, DotDash,
    DoubleSolid, DoubleDotted, DoubleDashed, DoubleDotDash
}

/// <summary>A named relation between two entities on the board.</summary>
public class BoardRelation
{
    public string         Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string         FromId      { get; set; } = "";
    public string         ToId        { get; set; } = "";
    /// <summary>Short label rendered on the connecting line.</summary>
    public string         Caption     { get; set; } = "";
    /// <summary>Entry shown in the board legend (empty = hidden from legend). Max 20 chars.</summary>
    public string         LegendLabel { get; set; } = "";
    public BoardLineStyle LineStyle   { get; set; } = BoardLineStyle.Solid;
    /// <summary>WPF theme brush resource key for the line color.</summary>
    public string         LineColor   { get; set; } = "AccentHighlightBrush";
    /// <summary>Stroke thickness in pixels (1–10).</summary>
    public double         Thickness   { get; set; } = 1.5;
}

/// <summary>
/// Board layout + relation graph for all entity types in a project.
/// Stored as a single _board_world.json file.
/// </summary>
public class EntityBoardData
{
    public Dictionary<string, BoardPosition> Positions     { get; set; } = new();
    public List<BoardRelation>               Relations     { get; set; } = [];
    /// <summary>Whether the floating legend panel is visible.</summary>
    public bool                              LegendVisible { get; set; } = true;
}

public static class EntityBoardService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The board now uses a single combined file for all entity types.
    /// Pass "_world" as entityType for the global board.
    /// Legacy per-type keys still work for reading old data.
    /// </summary>
    private static string BoardFilePath(string projFolder, string entityType)
    {
        var folderName = entityType.TrimEnd('s');
        if (string.IsNullOrEmpty(folderName)) folderName = entityType;
        return Path.Combine(projFolder, "PROJECTPLAN", $"_board_{folderName}.json");
    }

    public static EntityBoardData Load(string projFolder, string entityType)
    {
        var path = BoardFilePath(projFolder, entityType);
        if (!File.Exists(path)) return new EntityBoardData();
        try
        {
            return JsonSerializer.Deserialize<EntityBoardData>(File.ReadAllText(path), ReadOpts)
                   ?? new EntityBoardData();
        }
        catch { return new EntityBoardData(); }
    }

    public static void Save(string projFolder, string entityType, EntityBoardData data)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(projFolder, "PROJECTPLAN"));
            File.WriteAllText(BoardFilePath(projFolder, entityType),
                              JsonSerializer.Serialize(data, WriteOpts));
        }
        catch { }
    }
}
