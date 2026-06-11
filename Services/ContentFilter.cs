using System.Text;
using System.Text.RegularExpressions;

namespace ClaudetRelay.Services;

/// <summary>
/// Per-line sanitiser for text/document file content injected into model context.
/// Code-file extensions (configurable) bypass filtering entirely.
/// </summary>
public static partial class ContentFilter
{
    // Matches a Markdown table separator row:  |---|:---:|---| or plain ---
    [GeneratedRegex(@"^\|?[\s\-\|:]+\|[\s\-\|:]*$")]
    private static partial Regex TableDividerRx();

    // Matches a Markdown table data row (starts and ends with pipe, or has 2+ pipes)
    [GeneratedRegex(@"^\|.+\|$")]
    private static partial Regex TableRowRx();

    // HTML tags
    [GeneratedRegex(@"<[^>]{1,200}>")]
    private static partial Regex HtmlTagRx();

    // Inline base64 blocks (data URIs or raw ≥80-char base64 strings)
    [GeneratedRegex(@"data:[^;]{1,50};base64,[A-Za-z0-9+/=]+|(?:[A-Za-z0-9+/]{4}){20,}[A-Za-z0-9+/=]{0,3}")]
    private static partial Regex Base64Rx();

    // Markdown image tags  ![alt](url)  →  kept as [alt]
    [GeneratedRegex(@"!\[([^\]]*)\]\([^\)]*\)")]
    private static partial Regex ImageRx();

    // Markdown TOC links that are just anchors:  [text](#anchor)
    [GeneratedRegex(@"\[([^\]]+)\]\(#[^\)]*\)")]
    private static partial Regex TocLinkRx();

    // Unicode emoji (pictographs, symbols, transport, misc symbols, dingbats, supplemental)
    // Strips pictographic emoji while leaving technical symbols (→ × ° ± etc.) untouched.
    [GeneratedRegex(@"[☀-➿︀-️⌀-⏿]|[\uD83C-\uDBFF][\uDC00-\uDFFF]")]
    private static partial Regex EmojiRx();

    // Repeated whitespace on a single line
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRx();

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Filter <paramref name="content"/> for injection into model context.
    /// Returns the input unchanged when <paramref name="ext"/> is in the code-extension list.
    /// </summary>
    public static string Apply(string content, string ext)
    {
        if (IsCodeExtension(ext)) return content;

        var sb   = new StringBuilder(content.Length);
        int blank = 0;

        foreach (var rawLine in content.AsSpan().EnumerateLines())
        {
            var line = rawLine.ToString();

            // Drop table separator and data rows
            if (TableDividerRx().IsMatch(line)) continue;
            if (TableRowRx().IsMatch(line))
            {
                // Convert table row to plain pipe-free text
                line = string.Join("  ", line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            // Drop lines that are purely base64
            if (Base64Rx().IsMatch(line) && line.Trim().Length > 60) continue;

            // Image tags → alt text (or nothing if alt is empty)
            line = ImageRx().Replace(line, m =>
            {
                var alt = m.Groups[1].Value.Trim();
                return alt.Length > 0 ? $"[image: {alt}]" : "";
            });

            // TOC anchor links → just the label text
            line = TocLinkRx().Replace(line, m => m.Groups[1].Value);

            // Strip HTML tags
            line = HtmlTagRx().Replace(line, "");

            // Strip pictographic emoji
            line = EmojiRx().Replace(line, "");

            // Collapse runs of spaces/tabs
            line = MultiSpaceRx().Replace(line, " ").Trim();

            if (line.Length == 0)
            {
                blank++;
                if (blank > 1) continue;   // collapse consecutive blank lines to one
            }
            else
            {
                blank = 0;
            }

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HashSet<string>? _codeExts;

    /// <summary>Reload the extension set from current settings (call after settings save).</summary>
    public static void InvalidateCache() => _codeExts = null;

    private static bool IsCodeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return false;

        _codeExts ??= BuildExtSet(SettingsService.Load().CodeFileExtensions);

        var normalized = ext.StartsWith('.') ? ext : "." + ext;
        return _codeExts.Contains(normalized.ToLowerInvariant());
    }

    private static HashSet<string> BuildExtSet(string raw) =>
        raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant())
           .ToHashSet();
}
