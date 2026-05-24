using System.IO;
using System.Text;
using System.Xml.Linq;

namespace ClaudetRelay.Services;

// ── Roadmap data models ─────────────────────────────────────────────────────

public enum ItemStatus { Todo, InProgress, Done }

/// <summary>A single task / deliverable on the roadmap.</summary>
public class RoadmapItem
{
    public string     Id          { get; set; } = NewId();
    public string     Title       { get; set; } = "";
    public string     Description { get; set; } = "";
    public ItemStatus Status      { get; set; } = ItemStatus.Todo;
    public int        Progress    { get; set; } = 0;   // 0–100
    public string     CreatedBy   { get; set; } = "User";
    public DateTime   CreatedAt   { get; set; } = DateTime.UtcNow;
    public string     CompletedBy { get; set; } = "";
    public DateTime?  CompletedAt { get; set; }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>A grouping of items; its progress is the average of its items.</summary>
public class RoadmapMilestone
{
    public string            Id          { get; set; } = NewId();
    public string            Title       { get; set; } = "";
    public string            Description { get; set; } = "";
    public ItemStatus        Status      { get; set; } = ItemStatus.Todo;
    public List<RoadmapItem> Items       { get; set; } = [];
    public string            CreatedBy   { get; set; } = "User";
    public DateTime          CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime?         CompletedAt { get; set; }

    /// <summary>Average progress of all items. Zero if no items.</summary>
    public int Progress => Items.Count == 0 ? 0
        : (int)Math.Round(Items.Average(i => (double)i.Progress));

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>The full roadmap for one project.</summary>
public class Roadmap
{
    public DateTime               LastModified { get; set; } = DateTime.UtcNow;
    public List<RoadmapMilestone> Milestones   { get; set; } = [];
}

// ── Service ─────────────────────────────────────────────────────────────────

public static class RoadmapService
{
    private const string FileName = "roadmap.xml";

    // ── Status icons ───────────────────────────────────────────────────────

    public static string StatusIcon(ItemStatus s) => s switch
    {
        ItemStatus.InProgress => "🔄",
        ItemStatus.Done       => "✅",
        _                     => "⭕"
    };

    // ── Persistence ────────────────────────────────────────────────────────

    public static Roadmap Load(string projectFolder)
    {
        var path = Path.Combine(projectFolder, FileName);
        if (!File.Exists(path)) return new Roadmap();
        try
        {
            var doc     = XDocument.Load(path);
            var root    = doc.Root!;
            var roadmap = new Roadmap();

            if (root.Attribute("lastModified")?.Value is string lm)
                roadmap.LastModified = ParseDt(lm);

            foreach (var ms in root.Elements("Milestone"))
            {
                var milestone = new RoadmapMilestone
                {
                    Id          = ms.Attribute("id")?.Value          ?? NewId(),
                    Title       = ms.Attribute("title")?.Value       ?? "",
                    Description = ms.Attribute("description")?.Value ?? "",
                    Status      = ParseStatus(ms.Attribute("status")?.Value),
                    CreatedBy   = ms.Attribute("createdBy")?.Value   ?? "User",
                    CreatedAt   = ParseDt(ms.Attribute("createdAt")?.Value),
                    CompletedAt = ParseDtNull(ms.Attribute("completedAt")?.Value)
                };

                foreach (var it in ms.Elements("Item"))
                {
                    milestone.Items.Add(new RoadmapItem
                    {
                        Id          = it.Attribute("id")?.Value          ?? NewId(),
                        Title       = it.Attribute("title")?.Value       ?? "",
                        Description = it.Attribute("description")?.Value ?? "",
                        Status      = ParseStatus(it.Attribute("status")?.Value),
                        Progress    = int.TryParse(it.Attribute("progress")?.Value, out var p)
                                          ? Math.Clamp(p, 0, 100) : 0,
                        CreatedBy   = it.Attribute("createdBy")?.Value   ?? "User",
                        CreatedAt   = ParseDt(it.Attribute("createdAt")?.Value),
                        CompletedBy = it.Attribute("completedBy")?.Value ?? "",
                        CompletedAt = ParseDtNull(it.Attribute("completedAt")?.Value)
                    });
                }

                roadmap.Milestones.Add(milestone);
            }

            return roadmap;
        }
        catch { return new Roadmap(); }
    }

    public static void Save(string projectFolder, Roadmap roadmap)
    {
        roadmap.LastModified = DateTime.UtcNow;
        Directory.CreateDirectory(projectFolder);
        var path = Path.Combine(projectFolder, FileName);

        var root = new XElement("Roadmap",
            new XAttribute("lastModified", roadmap.LastModified.ToString("O")));

        foreach (var ms in roadmap.Milestones)
        {
            var msEl = new XElement("Milestone",
                new XAttribute("id",          ms.Id),
                new XAttribute("title",       ms.Title),
                new XAttribute("description", ms.Description),
                new XAttribute("status",      ms.Status.ToString()),
                new XAttribute("createdBy",   ms.CreatedBy),
                new XAttribute("createdAt",   ms.CreatedAt.ToString("O")),
                new XAttribute("completedAt", ms.CompletedAt?.ToString("O") ?? ""));

            foreach (var it in ms.Items)
            {
                msEl.Add(new XElement("Item",
                    new XAttribute("id",          it.Id),
                    new XAttribute("title",       it.Title),
                    new XAttribute("description", it.Description),
                    new XAttribute("status",      it.Status.ToString()),
                    new XAttribute("progress",    it.Progress),
                    new XAttribute("createdBy",   it.CreatedBy),
                    new XAttribute("createdAt",   it.CreatedAt.ToString("O")),
                    new XAttribute("completedBy", it.CompletedBy),
                    new XAttribute("completedAt", it.CompletedAt?.ToString("O") ?? "")));
            }

            root.Add(msEl);
        }

        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
    }

    // ── AI context ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a text summary of the roadmap for injection into AI system prompts.
    /// Includes update/complete command syntax appropriate for the AI's role.
    /// Returns empty string when the roadmap has no milestones.
    /// </summary>
    public static string GetContextText(Roadmap roadmap, bool isCoordinator)
    {
        if (roadmap.Milestones.Count == 0) return "";

        var sb = new StringBuilder("\n\n--- PROJECT ROADMAP ---\n");
        foreach (var ms in roadmap.Milestones)
        {
            sb.AppendLine($"{StatusIcon(ms.Status)} {ms.Title}  [{ms.Progress}%]");
            foreach (var it in ms.Items)
            {
                var done = it.Status == ItemStatus.Done
                    ? $" (completed by {it.CompletedBy})" : "";
                sb.AppendLine(
                    $"  {StatusIcon(it.Status)} [id:{it.Id}] {it.Title} — {it.Progress}%{done}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("To update an item's progress, embed this tag anywhere in your response:");
        sb.AppendLine("  [ROADMAP:update:ITEM_ID:PROGRESS]  — e.g. [ROADMAP:update:a1b2c3d4:75]");
        if (isCoordinator)
        {
            sb.AppendLine("As Coordinator, you may also formally complete an item:");
            sb.AppendLine("  [ROADMAP:complete:ITEM_ID]  — e.g. [ROADMAP:complete:a1b2c3d4]");
        }
        sb.AppendLine("Tags are stripped from the visible chat — only the roadmap state changes.");
        sb.AppendLine("--- END ROADMAP ---");
        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    private static ItemStatus ParseStatus(string? s) => s switch
    {
        "InProgress" => ItemStatus.InProgress,
        "Done"       => ItemStatus.Done,
        _            => ItemStatus.Todo
    };

    private static DateTime ParseDt(string? s) =>
        DateTime.TryParse(s, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : DateTime.UtcNow;

    private static DateTime? ParseDtNull(string? s) =>
        string.IsNullOrEmpty(s) ? null
        : DateTime.TryParse(s, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : null;
}
