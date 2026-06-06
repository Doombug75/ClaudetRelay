using System.IO;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;

namespace ClaudetRelay.Services;

/// <summary>
/// Renders Markdown content to a PDF file using PDFsharp.
/// No external tools (Adobe, Ghostscript, printer drivers) are required.
///
/// Supported Markdown elements:
///   # / ## / ###   — Headings (H1–H3, scaled sizes)
///   **bold**        — Bold inline text
///   *italic*        — Italic inline text
///   - / * / 1.      — Bullet and numbered lists
///   | col | col |   — Tables (header + data rows)
///   ```...```       — Code blocks (monospace, light background)
///   ---             — Horizontal rule
///   blank line      — Paragraph break
///
/// Output is A4, portrait, with standard margins.
/// </summary>
public static class PdfFileWriter
{
    // ── Page geometry ─────────────────────────────────────────────────────────

    private const double PageW      = 595;   // A4 width  in points
    private const double PageH      = 842;   // A4 height in points
    private const double MarginL    = 50;
    private const double MarginR    = 50;
    private const double MarginT    = 50;
    private const double MarginB    = 50;
    private const double TextWidth  = PageW - MarginL - MarginR;   // 495 pt

    // ── Font sizes ────────────────────────────────────────────────────────────

    private const double SizeH1     = 20;
    private const double SizeH2     = 16;
    private const double SizeH3     = 13;
    private const double SizeBody   = 11;
    private const double SizeCode   = 10;
    private const double LineH      = 16;    // line height for body text
    private const double ParaGap    =  8;    // extra gap after a paragraph

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Converts <paramref name="markdownContent"/> to a PDF and saves it to
    /// <paramref name="filePath"/>.  Returns false and sets <paramref name="error"/>
    /// on failure.
    /// </summary>
    public static bool TryWrite(string filePath, string markdownContent, out string? error)
    {
        error = null;
        try
        {
            var doc = new PdfDocument();
            doc.Info.Title   = Path.GetFileNameWithoutExtension(filePath);
            doc.Info.Creator = "ClaudetRelay";

            var ctx = new RenderContext(doc);
            RenderMarkdown(ctx, markdownContent);

            doc.Save(filePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── Render context ────────────────────────────────────────────────────────

    private sealed class RenderContext
    {
        public PdfDocument  Doc     { get; }
        public PdfPage      Page    { get; private set; }
        public XGraphics    Gfx     { get; private set; }
        public double       Y       { get; set; }

        // Fonts — created once, reused
        public XFont FontBody       { get; } = new XFont("Arial",          SizeBody,  XFontStyleEx.Regular);
        public XFont FontBold       { get; } = new XFont("Arial",          SizeBody,  XFontStyleEx.Bold);
        public XFont FontItalic     { get; } = new XFont("Arial",          SizeBody,  XFontStyleEx.Italic);
        public XFont FontH1         { get; } = new XFont("Arial",          SizeH1,    XFontStyleEx.Bold);
        public XFont FontH2         { get; } = new XFont("Arial",          SizeH2,    XFontStyleEx.Bold);
        public XFont FontH3         { get; } = new XFont("Arial",          SizeH3,    XFontStyleEx.Bold);
        public XFont FontCode       { get; } = new XFont("Courier New",    SizeCode,  XFontStyleEx.Regular);
        public XFont FontCodeBold   { get; } = new XFont("Courier New",    SizeCode,  XFontStyleEx.Bold);

        public RenderContext(PdfDocument doc)
        {
            Doc  = doc;
            Page = doc.AddPage();
            Page.Width  = PageW;
            Page.Height = PageH;
            Gfx  = XGraphics.FromPdfPage(Page);
            Y    = MarginT;
        }

        /// <summary>Adds a new page and resets the cursor.</summary>
        public void NewPage()
        {
            Gfx.Dispose();
            Page = Doc.AddPage();
            Page.Width  = PageW;
            Page.Height = PageH;
            Gfx  = XGraphics.FromPdfPage(Page);
            Y    = MarginT;
        }

        /// <summary>Ensures at least <paramref name="needed"/> vertical space remains.</summary>
        public void EnsureSpace(double needed)
        {
            if (Y + needed > PageH - MarginB) NewPage();
        }

        /// <summary>Advances the cursor by <paramref name="amount"/> pts, adding a page if needed.</summary>
        public void Advance(double amount)
        {
            Y += amount;
            if (Y > PageH - MarginB) NewPage();
        }
    }

    // ── Markdown renderer ─────────────────────────────────────────────────────

    private static void RenderMarkdown(RenderContext ctx, string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // ── Fenced code block ──────────────────────────────────────────
            if (line.TrimStart().StartsWith("```"))
            {
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    codeLines.Add(lines[i++]);
                i++; // skip closing ```
                RenderCodeBlock(ctx, codeLines);
                continue;
            }

            // ── Horizontal rule ────────────────────────────────────────────
            if (Regex.IsMatch(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                ctx.EnsureSpace(12);
                ctx.Gfx.DrawLine(XPens.LightGray,
                    new XPoint(MarginL, ctx.Y + 5),
                    new XPoint(MarginL + TextWidth, ctx.Y + 5));
                ctx.Advance(14);
                i++;
                continue;
            }

            // ── Table ──────────────────────────────────────────────────────
            if (line.TrimStart().StartsWith('|'))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                    tableLines.Add(lines[i++]);
                RenderTable(ctx, tableLines);
                continue;
            }

            // ── Headings ───────────────────────────────────────────────────
            if (line.StartsWith("### "))
            {
                RenderHeading(ctx, line[4..], ctx.FontH3, SizeH3 + 6);
                i++; continue;
            }
            if (line.StartsWith("## "))
            {
                RenderHeading(ctx, line[3..], ctx.FontH2, SizeH2 + 8);
                i++; continue;
            }
            if (line.StartsWith("# "))
            {
                RenderHeading(ctx, line[2..], ctx.FontH1, SizeH1 + 10);
                i++; continue;
            }

            // ── List items ─────────────────────────────────────────────────
            var listMatch = Regex.Match(line, @"^(\s*)([-*]|\d+\.)\s+(.*)");
            if (listMatch.Success)
            {
                int indent = listMatch.Groups[1].Length;
                bool numbered = char.IsDigit(listMatch.Groups[2].Value[0]);
                var text = listMatch.Groups[3].Value;
                var bullet = numbered ? "•" : "•";
                RenderParagraph(ctx, $"  {bullet}  {text}", ctx.FontBody, XBrushes.Black,
                    indent: 10 + indent * 4);
                i++; continue;
            }

            // ── Blank line → paragraph gap ─────────────────────────────────
            if (string.IsNullOrWhiteSpace(line))
            {
                ctx.Advance(ParaGap);
                i++; continue;
            }

            // ── Normal paragraph ───────────────────────────────────────────
            RenderParagraph(ctx, line, ctx.FontBody, XBrushes.Black);
            i++;
        }
    }

    // ── Element renderers ─────────────────────────────────────────────────────

    private static void RenderHeading(RenderContext ctx, string text, XFont font, double preGap)
    {
        ctx.EnsureSpace(preGap + LineH + 4);
        ctx.Advance(preGap * 0.4);
        var clean = StripInlineMarkdown(text);
        ctx.Gfx.DrawString(clean, font, XBrushes.Black,
            new XRect(MarginL, ctx.Y, TextWidth, LineH * 2), XStringFormats.TopLeft);
        ctx.Advance(font.Size + 6);
    }

    private static void RenderParagraph(
        RenderContext ctx, string text, XFont font, XBrush brush, double indent = 0)
    {
        // Strip inline markdown for a clean pass through XTextFormatter
        var clean = StripInlineMarkdown(text);
        if (string.IsNullOrWhiteSpace(clean)) return;

        // Estimate height needed (chars per line ≈ TextWidth / (fontSize * 0.55))
        double usableW   = TextWidth - indent;
        int    charsPerLine = Math.Max(1, (int)(usableW / (font.Size * 0.55)));
        int    lineCount    = (int)Math.Ceiling((double)clean.Length / charsPerLine);
        double neededH   = lineCount * LineH + 4;

        ctx.EnsureSpace(neededH);

        var tf   = new XTextFormatter(ctx.Gfx);
        var rect = new XRect(MarginL + indent, ctx.Y, usableW, neededH + LineH);
        tf.DrawString(clean, font, brush, rect, XStringFormats.TopLeft);
        ctx.Advance(neededH);
    }

    private static void RenderCodeBlock(RenderContext ctx, List<string> codeLines)
    {
        if (codeLines.Count == 0) return;

        double blockH = codeLines.Count * (SizeCode + 3) + 10;
        ctx.EnsureSpace(blockH + 6);
        ctx.Advance(4);

        // Light grey background
        var bgRect = new XRect(MarginL, ctx.Y, TextWidth, blockH);
        ctx.Gfx.DrawRoundedRectangle(
            new XSolidBrush(XColor.FromArgb(230, 230, 230)),
            bgRect, new XSize(4, 4));

        double lineY = ctx.Y + 6;
        foreach (var codeLine in codeLines)
        {
            ctx.Gfx.DrawString(codeLine, ctx.FontCode, XBrushes.Black,
                new XPoint(MarginL + 8, lineY));
            lineY += SizeCode + 3;
        }

        ctx.Advance(blockH + 6);
    }

    private static void RenderTable(RenderContext ctx, List<string> tableLines)
    {
        if (tableLines.Count < 2) return;

        // Parse cells — skip separator row (---)
        var dataRows = tableLines
            .Where(l => !Regex.IsMatch(l, @"^\s*\|[\s\-|:]+\|\s*$"))
            .Select(l => l.Trim('|', ' ')
                          .Split('|')
                          .Select(c => c.Trim())
                          .ToArray())
            .ToList();

        if (dataRows.Count == 0) return;

        int  cols      = dataRows.Max(r => r.Length);
        double colW    = TextWidth / cols;
        double rowH    = LineH + 6;
        double tableH  = dataRows.Count * rowH + 2;

        ctx.EnsureSpace(tableH + 10);
        ctx.Advance(4);

        double startY = ctx.Y;

        for (int r = 0; r < dataRows.Count; r++)
        {
            double rowY  = startY + r * rowH;
            bool isHeader = r == 0;

            // Header background
            if (isHeader)
            {
                ctx.Gfx.DrawRectangle(
                    new XSolidBrush(XColor.FromArgb(220, 220, 220)),
                    new XRect(MarginL, rowY, TextWidth, rowH));
            }

            // Draw row border
            ctx.Gfx.DrawRectangle(XPens.Gray,
                new XRect(MarginL, rowY, TextWidth, rowH));

            // Draw cell text
            var row = dataRows[r];
            for (int c = 0; c < cols; c++)
            {
                var cellText = c < row.Length ? row[c] : "";
                var font     = isHeader ? ctx.FontBold : ctx.FontBody;
                var cellRect = new XRect(MarginL + c * colW + 4, rowY + 2,
                                         colW - 8, rowH - 2);
                ctx.Gfx.DrawString(cellText, font, XBrushes.Black,
                    cellRect, XStringFormats.CenterLeft);

                // Vertical cell separator
                if (c > 0)
                    ctx.Gfx.DrawLine(XPens.LightGray,
                        new XPoint(MarginL + c * colW, rowY),
                        new XPoint(MarginL + c * colW, rowY + rowH));
            }
        }

        ctx.Advance(tableH + 8);
    }

    // ── Inline Markdown stripper ──────────────────────────────────────────────

    /// <summary>
    /// Removes inline Markdown syntax (*bold*, _italic_, `code`, [text](url))
    /// so PDFsharp receives clean plain text.
    /// A future version could render these as styled runs.
    /// </summary>
    private static string StripInlineMarkdown(string text)
    {
        // Bold: **text** or __text__
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.+?)__",     "$1");
        // Italic: *text* or _text_
        text = Regex.Replace(text, @"\*(.+?)\*",     "$1");
        text = Regex.Replace(text, @"_(.+?)_",       "$1");
        // Inline code: `text`
        text = Regex.Replace(text, @"`(.+?)`",        "$1");
        // Links: [text](url)
        text = Regex.Replace(text, @"\[(.+?)\]\(.+?\)", "$1");
        // Strikethrough: ~~text~~
        text = Regex.Replace(text, @"~~(.+?)~~",     "$1");
        return text;
    }
}
