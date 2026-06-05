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

    /// <summary>
    /// Filename (not full path) of the portrait image stored in PROJECTPLAN/_portraits/.
    /// Empty = no portrait. Use <see cref="WorldEntityService.GetPortraitPath"/> to resolve.
    /// </summary>
    public string PortraitFileName { get; set; } = "";

    /// <summary>
    /// Filename (not full path) of an attached reference image stored in PROJECTPLAN/_images/.
    /// Used for locations, lore, etc. — image is kept at full resolution.
    /// Empty = none. Use <see cref="WorldEntityService.GetImagePath"/> to resolve.
    /// </summary>
    public string ImageFileName { get; set; } = "";

    // ── Faction-specific ──────────────────────────────────────────────────

    /// <summary>
    /// Hex colour string for the faction dot badge (e.g. "#E53935"). Faction entities only.
    /// Empty = no colour assigned yet.
    /// </summary>
    public string FactionColor { get; set; } = "";

    /// <summary>
    /// IDs of member entities (Characters etc.) that belong to this faction.
    /// Also used by Lore to list characters who know this lore.
    /// </summary>
    public List<string> MemberIds { get; set; } = [];

    /// <summary>
    /// IDs of factions associated with this entity.
    /// For Lore: factions where every member shares this knowledge.
    /// </summary>
    public List<string> FactionIds { get; set; } = [];
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
            ("Arc",              "Story arc this location is tied to"),
            ("Alignment",        "friendly / hostile / neutral / contested …"),
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
            ("Arc",         "Story arc this lore belongs to"),
            ("Description", "What is this lore entry?"),
            ("Source",      "Where does this knowledge come from?")
            // CommonKnowledge, HistoricalKnowledge stored in Fields; Characters/Factions via chip sections
        ]
    };

    /// <summary>Returns the schema for the given entity type, or an empty list if unknown.</summary>
    public static List<(string Field, string Hint)> For(string entityType) =>
        All.TryGetValue(entityType, out var schema) ? schema : [];

    /// <summary>Returns the localised display name for an entity type key (e.g. "Character" → "Charakter").</summary>
    public static string LocalizeEntityType(string entityType) => entityType switch
    {
        "Character" => ClaudetRelay.Properties.Loc.S("EntityType_Character"),
        "Faction"   => ClaudetRelay.Properties.Loc.S("EntityType_Faction"),
        "Location"  => ClaudetRelay.Properties.Loc.S("EntityType_Location"),
        "Lore"      => ClaudetRelay.Properties.Loc.S("EntityType_Lore"),
        _           => entityType
    };

    /// <summary>
    /// Returns a localised display label for a schema field name.
    /// Falls back to the original field name for unsupported languages or unknown fields.
    /// </summary>
    public static string LocalizeSchemaField(string field) =>
        System.Threading.Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName == "de"
            ? field switch
            {
                "Role"               => "Rolle",
                "Age"                => "Alter",
                "Level / Classes"    => "Level / Klassen",
                "Alignment"          => "Gesinnung",
                "Background"         => "Hintergrund",
                "Goal"               => "Ziel",
                "Flaw"               => "Schwäche",
                "Arc"                => "Charakterbogen",
                "Voice"              => "Stimme / Sprechweise",
                "Health / Resources" => "HP / Ressourcen",
                "Attributes"         => "Attribute",
                "Skills"             => "Fähigkeiten",
                "Type"               => "Typ",
                "Description"        => "Beschreibung",
                "Atmosphere"         => "Atmosphäre",
                "Significance"       => "Bedeutung",
                "First appears in"   => "Erste Erwähnung",
                "Leader"             => "Anführer",
                "Territory"          => "Territorium",
                "Category"           => "Kategorie",
                "Source"             => "Quelle",
                _                    => field
            }
            : field;

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
        // ── 5 additions ──────────────────────────────────────────────────────
        "#FFC107", // Amber (warm gold — distinct from orange)
        "#689F38", // Olive Green (forest, distinct from bright green)
        "#00838F", // Deep Cyan (between cyan and teal)
        "#AD1457", // Deep Pink / Crimson
        "#EF6C00", // Burnt Orange (deep, red-orange)
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
        var dir        = GetEntityFolder(projFolder, entity.EntityType);
        Directory.CreateDirectory(dir);
        var targetPath = EntityFilePath(projFolder, entity);

        // Clean up any stale files that share the same entity Id but differ in filename.
        // This can happen when an entity was created externally (e.g. via MCP) with a
        // filename containing spaces, while MakeSafeName would produce an underscore version.
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            if (string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var text = File.ReadAllText(f);
                if (text.Contains($"\"Id\": \"{entity.Id}\""))
                    File.Delete(f);
            }
            catch { /* best-effort */ }
        }

        File.WriteAllText(targetPath, JsonSerializer.Serialize(entity, WriteOpts));
    }

    /// <summary>
    /// Renames an entity: deletes the old JSON, renames the portrait file if present, then saves.
    /// </summary>
    public static void Rename(string projFolder, WorldEntity entity, string oldName)
    {
        // Remove old entity JSON
        var oldPath = Path.Combine(GetEntityFolder(projFolder, entity.EntityType),
                                   MakeSafeName(oldName) + ".json");
        if (File.Exists(oldPath)) File.Delete(oldPath);

        string newSafeName = MakeSafeName(entity.Name);

        // Rename portrait file
        if (!string.IsNullOrWhiteSpace(entity.PortraitFileName))
        {
            var oldPath2 = GetPortraitPath(projFolder, entity.PortraitFileName);
            if (File.Exists(oldPath2))
            {
                var ext2        = Path.GetExtension(entity.PortraitFileName);
                var newFileName = $"{newSafeName}_{entity.Id}{ext2}";
                var newPath2    = GetPortraitPath(projFolder, newFileName);
                try { File.Move(oldPath2, newPath2, overwrite: true); entity.PortraitFileName = newFileName; }
                catch { }
            }
        }

        // Rename attached reference image
        if (!string.IsNullOrWhiteSpace(entity.ImageFileName))
        {
            var oldPath3 = GetImagePath(projFolder, entity.ImageFileName);
            if (File.Exists(oldPath3))
            {
                var ext3        = Path.GetExtension(entity.ImageFileName);
                var newFileName = $"{newSafeName}_{entity.Id}{ext3}";
                var newPath3    = GetImagePath(projFolder, newFileName);
                try { File.Move(oldPath3, newPath3, overwrite: true); entity.ImageFileName = newFileName; }
                catch { }
            }
        }

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

    /// <summary>Deletes the entity file and any associated portrait. Silent if files do not exist.</summary>
    public static void Delete(string projFolder, WorldEntity entity)
    {
        // Delete entity JSON
        var path = EntityFilePath(projFolder, entity);
        if (File.Exists(path)) File.Delete(path);

        // Delete portrait
        var portraitsDir = GetPortraitsFolder(projFolder);
        if (Directory.Exists(portraitsDir))
        {
            var toDelete = Directory.GetFiles(portraitsDir, $"*_{entity.Id}.*")
                           .Concat(Directory.GetFiles(portraitsDir, entity.Id + ".*"));
            if (!string.IsNullOrWhiteSpace(entity.PortraitFileName))
                toDelete = toDelete.Append(GetPortraitPath(projFolder, entity.PortraitFileName));
            foreach (var f in toDelete.Distinct())
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        // Delete attached reference image
        var imagesDir = GetImagesFolder(projFolder);
        if (Directory.Exists(imagesDir))
        {
            var toDelete = Directory.GetFiles(imagesDir, $"*_{entity.Id}.*")
                           .Concat(Directory.GetFiles(imagesDir, entity.Id + ".*"));
            if (!string.IsNullOrWhiteSpace(entity.ImageFileName))
                toDelete = toDelete.Append(GetImagePath(projFolder, entity.ImageFileName));
            foreach (var f in toDelete.Distinct())
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    // ── Helper ─────────────────────────────────────────────────────────────

    public static string GetPortraitsFolder(string projFolder) =>
        Path.Combine(projFolder, "PROJECTPLAN", "_portraits");

    /// <summary>Returns the full path to an entity's portrait file (may not exist yet).</summary>
    public static string GetPortraitPath(string projFolder, string fileName) =>
        Path.Combine(GetPortraitsFolder(projFolder), fileName);

    public static string GetImagesFolder(string projFolder) =>
        Path.Combine(projFolder, "PROJECTPLAN", "_images");

    /// <summary>Returns the full path to an entity's attached reference image (may not exist yet).</summary>
    public static string GetImagePath(string projFolder, string fileName) =>
        Path.Combine(GetImagesFolder(projFolder), fileName);

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
    public double X          { get; set; } = 60;
    public double Y          { get; set; } = 60;
    /// <summary>User-resized card width. 0 = use default.</summary>
    public double CardWidth  { get; set; } = 0;
    /// <summary>User-resized card height. 0 = auto.</summary>
    public double CardHeight { get; set; } = 0;
}

/// <summary>Visual style of a relation line on the board.</summary>
public enum BoardLineStyle
{
    Solid, Dotted, Dashed, DotDash,
    DoubleSolid, DoubleDotted, DoubleDashed, DoubleDotDash
}

/// <summary>A movable waypoint on a relation line (makes the line bend around cards).</summary>
public class BoardWaypoint
{
    public string  Id         { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double  X          { get; set; }
    public double  Y          { get; set; }
    /// <summary>
    /// When set, this waypoint's position is always read from the master waypoint with this ID
    /// (which lives on another relation).  Used so junction-connected lines move together.
    /// </summary>
    public string? LinkedToId { get; set; } = null;
}

/// <summary>A named line-style preset that lives in the board legend.</summary>
public class BoardLinePreset
{
    public string         Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string         Name      { get; set; } = "Standard";
    public BoardLineStyle Style     { get; set; } = BoardLineStyle.Solid;
    public string         Color     { get; set; } = "#2196F3";
    public double         Thickness { get; set; } = 1.5;
    public bool           HasArrow  { get; set; } = false;
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
    /// <summary>Hex color string (#RRGGBB) for the line. Legacy boards may store a brush key name.</summary>
    public string         LineColor   { get; set; } = "#2196F3";
    /// <summary>Stroke thickness in pixels (1–10).</summary>
    public double         Thickness   { get; set; } = 1.5;
    /// <summary>When true, an arrowhead is drawn at the ToId end of the line.</summary>
    public bool           HasArrow    { get; set; } = true;
    /// <summary>Ordered intermediate waypoints (bends). Line goes From → WP[0] → … → To.</summary>
    public List<BoardWaypoint> Waypoints { get; set; } = [];
    /// <summary>When true the line visually STARTS at Waypoints[0] instead of the FromId card border.</summary>
    public bool StartsAtJunction { get; set; } = false;
    /// <summary>When true the line visually ENDS at Waypoints[^1] instead of the ToId card border.</summary>
    public bool EndsAtJunction   { get; set; } = false;
}

/// <summary>A freely-placeable styled text annotation on the board canvas.</summary>
public class BoardTextBox
{
    public string Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double X           { get; set; }
    public double Y           { get; set; }
    public double Width       { get; set; } = 200;
    public double Height      { get; set; } = 80;
    public string Text        { get; set; } = "Text";
    public string FontFamily  { get; set; } = "Segoe UI";
    public double FontSize    { get; set; } = 12;
    public bool   Bold        { get; set; } = false;
    public bool   Italic      { get; set; } = false;
    public string TextColor   { get; set; } = "#212121";
    public string BgColor     { get; set; } = "#00000000";
    public string FrameColor  { get; set; } = "#FF808080";
    public double FrameThick  { get; set; } = 0;
    public string FrameStyle  { get; set; } = "None"; // None, Solid, Dashed, Dotted
    public string HAlign      { get; set; } = "Left";  // Left, Center, Right, Justify
    public string VAlign      { get; set; } = "Top";   // Top, Center, Bottom
}

/// <summary>A colored grouping frame that groups and moves items on the board canvas.</summary>
public class BoardFrame
{
    public string Id     { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double X      { get; set; }
    public double Y      { get; set; }
    public double Width  { get; set; } = 300;
    public double Height { get; set; } = 200;
    public string Color  { get; set; } = "#FF4488AA";
    public string Label  { get; set; } = "";
}

/// <summary>
/// Board layout + relation graph stored per board ID.
/// File: PROJECTPLAN/_board_{boardId}.json
/// </summary>
public class EntityBoardData
{
    public Dictionary<string, BoardPosition> Positions        { get; set; } = new();
    public List<BoardRelation>               Relations        { get; set; } = [];
    /// <summary>Named line-style presets managed via the legend editor.</summary>
    public List<BoardLinePreset>             LinePresets      { get; set; } = [];
    /// <summary>Freely placed text annotations on the board.</summary>
    public List<BoardTextBox>                TextBoxes        { get; set; } = [];
    /// <summary>Board IDs pinned onto this board as tiles (board-in-board). Key = board ID.</summary>
    public Dictionary<string, BoardPosition> BoardPinPositions { get; set; } = new();
    /// <summary>Colored grouping frames on the canvas.</summary>
    public List<BoardFrame>                  Frames            { get; set; } = [];
    /// <summary>Whether the floating legend panel is visible.</summary>
    public bool                              LegendVisible    { get; set; } = true;
    /// <summary>Snap card/waypoint/textbox movements to this grid (px). 0 = off.</summary>
    public double                            GridSize         { get; set; } = 10;
    /// <summary>When true the snap behaviour is active.</summary>
    public bool                              SnapToGrid       { get; set; } = false;
    /// <summary>When true grid lines are rendered (independent of snap).</summary>
    public bool                              GridVisible      { get; set; } = false;
    /// <summary>Grid line colour as "#AARRGGBB". Null = use the default theme colour.</summary>
    public string?                           GridColor        { get; set; }
    /// <summary>Absolute path to the board background image, or null.</summary>
    public string?                           BackgroundImagePath { get; set; }
    /// <summary>How the background image fills the canvas: Fill | Uniform | UniformToFill | Scale.</summary>
    public string                            BgImageMode      { get; set; } = "Fill";
    /// <summary>Scale percentage used when BgImageMode = "Scale" (1 – 500).</summary>
    public double                            BgImageScale     { get; set; } = 100;
}

public static class EntityBoardService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    private static string BoardFilePath(string projFolder, string boardId) =>
        Path.Combine(projFolder, "PROJECTPLAN", $"_board_{boardId}.json");

    /// <summary>Loads board data by board ID. Migrates legacy _board__world.json for the default board.</summary>
    public static EntityBoardData Load(string projFolder, string boardId)
    {
        var path = BoardFilePath(projFolder, boardId);
        if (File.Exists(path))
        {
            try { return JsonSerializer.Deserialize<EntityBoardData>(File.ReadAllText(path), ReadOpts) ?? new EntityBoardData(); }
            catch { return new EntityBoardData(); }
        }

        // Legacy migration: the original board was written as _board__world.json
        if (boardId == WorldBoardRegistryService.DefaultBoardId)
        {
            var legacy = Path.Combine(projFolder, "PROJECTPLAN", "_board__world.json");
            if (File.Exists(legacy))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<EntityBoardData>(File.ReadAllText(legacy), ReadOpts)
                               ?? new EntityBoardData();
                    Save(projFolder, boardId, data);   // migrate to new path
                    File.Delete(legacy);
                    return data;
                }
                catch { }
            }
        }
        return new EntityBoardData();
    }

    public static void Save(string projFolder, string boardId, EntityBoardData data)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(projFolder, "PROJECTPLAN"));
            File.WriteAllText(BoardFilePath(projFolder, boardId),
                              JsonSerializer.Serialize(data, WriteOpts));
        }
        catch { }
    }
}

// ── World board registry ───────────────────────────────────────────────────

/// <summary>A named canvas board showing a configurable set of entity types.</summary>
public class WorldBoard
{
    public string       Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string       Name        { get; set; } = "Board";
    public string       Symbol      { get; set; } = "🗺";
    /// <summary>Singular entity type names shown on this board (e.g. "Character", "Location").</summary>
    public List<string> EntityTypes { get; set; } = ["Character", "Location", "Faction", "Lore"];
    public DateTime     CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime     UpdatedAt   { get; set; } = DateTime.UtcNow;
}

public static class WorldBoardRegistryService
{
    /// <summary>Fixed ID used by the default "Main Board" (enables legacy data migration).</summary>
    public const string DefaultBoardId = "world001";

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    private static string RegistryPath(string projFolder) =>
        Path.Combine(projFolder, "PROJECTPLAN", "_boards.json");

    public static List<WorldBoard> Load(string projFolder)
    {
        var path = RegistryPath(projFolder);
        if (File.Exists(path))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<WorldBoard>>(File.ReadAllText(path), ReadOpts);
                if (list is not null) return list;   // may be empty — that's fine
            }
            catch { }
        }

        // No boards file — return empty list; gallery shows the "create first board" prompt
        return [];
    }

    public static void Save(string projFolder, List<WorldBoard> boards)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(projFolder, "PROJECTPLAN"));
            File.WriteAllText(RegistryPath(projFolder),
                JsonSerializer.Serialize(boards, WriteOpts));
        }
        catch { }
    }

    public static readonly string[] SymbolPalette =
    [
        // World / places
        "🗺", "🌍", "🌐", "🏰", "🏛️", "🏠", "🏕", "⛩", "🕌", "🗻", "🌋", "🏝",
        // Nature / weather
        "🌿", "🌳", "🌾", "🌵", "🌴", "🌸", "🌺", "🌙", "☀️", "🌊", "🏔️", "❄️",
        "🔥", "⚡", "🌪", "🌈", "💫", "🌀",
        // Combat / military
        "⚔️", "🗡️", "🛡️", "🏹", "⚓", "🚩",
        // Characters / social
        "👥", "👑", "🧙", "💀", "🦅", "🕊", "🦁", "🐺", "🐍", "🐉",
        // Magic / knowledge
        "🔮", "📜", "📖", "🗝️", "⭐", "🔯", "🪄", "🧿", "⚗", "🔭", "💎",
        // Symbols / misc
        "⚜", "🔱", "🏴", "⚙", "🧲", "🪨", "🗿", "🎭", "🎪"
    ];
}
