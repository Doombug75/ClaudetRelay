using System.IO;
using System.Text.Json;

namespace ClaudetRelay.Services;

/// <summary>
/// Rolling chat log for general (non-project) chat.
///
/// Layout on disk:
///   GeneralChat/chatlog.json      ← current segment  (≤ 500 entries)
///   GeneralChat/chatlog-prev.json ← previous segment (≤ 500 entries)
///   GeneralChat/summary.md        ← AI-generated summaries of older segments
///
/// On rotation (chatlog.json full):
///   1. The existing chatlog-prev.json is read and returned as <em>displaced entries</em>
///      so the caller can ask an AI to summarise them.
///   2. chatlog.json is promoted to chatlog-prev.json (overwriting the old one).
///   3. A fresh chatlog.json is started.
///
/// This keeps exactly two JSON segment files on disk at all times.
/// The summary.md file accumulates AI-written summaries of all displaced segments
/// and is periodically compressed by the caller.
/// </summary>
public static class GeneralChatLogService
{
    // ── Paths ──────────────────────────────────────────────────────────────

    public static readonly string LogFolder = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "GeneralChat");

    private static string CurrentPath => Path.Combine(LogFolder, "chatlog.json");
    private static string PrevPath    => Path.Combine(LogFolder, "chatlog-prev.json");
    private static string SummaryPath => Path.Combine(LogFolder, "summary.md");

    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    // ── Append ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends <paramref name="entry"/> to the current log segment.
    /// Returns <c>true</c> when a rotation occurred.
    /// When a rotation occurs, <paramref name="displaced"/> is set to the entries
    /// that were previously in chatlog-prev.json (if any) — the caller should
    /// ask an AI to summarise them and append to summary.md.
    /// </summary>
    public static bool AppendEntry(ChatLogEntry entry, out List<ChatLogEntry>? displaced)
    {
        displaced = null;
        try
        {
            Directory.CreateDirectory(LogFolder);
            var current = LoadFile(CurrentPath);

            if (current.Count >= MaxEntries)
            {
                // Grab the previous segment's entries so the caller can summarise them
                if (File.Exists(PrevPath))
                    displaced = LoadFile(PrevPath);

                // Promote current → prev (overwrites the old prev)
                File.Copy(CurrentPath, PrevPath, overwrite: true);

                // Start fresh
                current = [];
            }

            current.Add(entry);
            File.WriteAllText(CurrentPath, JsonSerializer.Serialize(current, WriteOpts));
            return displaced is not null; // only true on 2nd+ rotation (when prev existed)
        }
        catch { return false; }
    }

    // ── Load ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all exportable entries in order: prev segment then current segment.
    /// </summary>
    public static List<ChatLogEntry> LoadRecentLog()
    {
        var result = new List<ChatLogEntry>();
        if (File.Exists(PrevPath))    result.AddRange(LoadFile(PrevPath));
        if (File.Exists(CurrentPath)) result.AddRange(LoadFile(CurrentPath));
        return result;
    }

    // ── Summary.md ─────────────────────────────────────────────────────────

    /// <summary>Returns the full contents of summary.md, or null if it does not exist.</summary>
    public static string? ReadSummary()
        => File.Exists(SummaryPath) ? File.ReadAllText(SummaryPath) : null;

    /// <summary>Appends <paramref name="section"/> (a dated summary block) to summary.md.</summary>
    public static void AppendToSummary(string section)
    {
        Directory.CreateDirectory(LogFolder);
        File.AppendAllText(SummaryPath, "\n\n" + section);
    }

    /// <summary>Replaces the entire contents of summary.md with <paramref name="newContent"/>.</summary>
    public static void ReplaceSummary(string newContent)
    {
        Directory.CreateDirectory(LogFolder);
        File.WriteAllText(SummaryPath, newContent);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static List<ChatLogEntry> LoadFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ChatLogEntry>>(
                       File.ReadAllText(path), ReadOpts) ?? [];
        }
        catch { return []; }
    }
}
