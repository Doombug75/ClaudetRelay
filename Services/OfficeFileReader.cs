using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClaudetRelay.Services;

/// <summary>
/// Extracts human-readable text from modern Office and OpenDocument files.
///
/// All supported formats are ZIP archives — no external NuGet packages are required.
/// Tables are rendered as Markdown tables so AI models can read structured data naturally.
///
/// Supported formats:
///   .docx  — Word 2007+         (paragraphs, headings, tables)
///   .xlsx  — Excel 2007+        (each sheet as a Markdown table)
///   .pptx  — PowerPoint 2007+   (slide text, grouped by slide)
///   .odt   — LibreOffice Writer  (paragraphs, headings, lists, tables)
///   .ods   — LibreOffice Calc    (each sheet as a Markdown table)
///   .odp   — LibreOffice Impress (slide text, grouped by slide)
///
/// Legacy binary formats (.doc, .xls, .ppt) are NOT supported.
/// Users should re-save files in a modern format before asking models to read them.
/// </summary>
public static class OfficeFileReader
{
    private static readonly HashSet<string> _supported = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp" };

    /// <summary>Returns true if the file extension is a supported Office/ODF format.</summary>
    public static bool IsSupported(string filePath)
        => _supported.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// Extracts human-readable text from an Office or ODF file.
    /// Returns null if the format is not supported.
    /// Returns a user-friendly error string (never throws) if the file is corrupt/unreadable.
    /// </summary>
    public static string? TryExtractText(string filePath)
    {
        if (!IsSupported(filePath)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            return Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".docx" => ExtractDocx(zip),
                ".xlsx" => ExtractXlsx(zip),
                ".pptx" => ExtractPptx(zip),
                ".odt"  => ExtractOdf(zip, OdfMode.Text),
                ".ods"  => ExtractOdf(zip, OdfMode.Spreadsheet),
                ".odp"  => ExtractOdf(zip, OdfMode.Presentation),
                _       => null
            };
        }
        catch (Exception ex)
        {
            return $"[Could not read file: {ex.Message}]";
        }
    }

    // ── DOCX (Word) ───────────────────────────────────────────────────────────

    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string ExtractDocx(ZipArchive zip)
    {
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return "[No document.xml found in .docx]";

        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        var body = doc.Root?.Element(W + "body");
        if (body is null) return "[Empty document]";

        var sb = new StringBuilder();
        foreach (var el in body.Elements())
        {
            if (el.Name == W + "p")
            {
                // Paragraph — concatenate all text runs
                sb.AppendLine(string.Concat(el.Descendants(W + "t").Select(t => t.Value)));
            }
            else if (el.Name == W + "tbl")
            {
                // Table → Markdown
                sb.AppendLine();
                sb.AppendLine(ExtractDocxTable(el));
                sb.AppendLine();
            }
        }
        return sb.ToString().Trim();
    }

    private static string ExtractDocxTable(XElement tbl)
    {
        var rows = tbl.Elements(W + "tr")
            .Select(row => row.Elements(W + "tc")
                .Select(cell => string.Concat(cell.Descendants(W + "t").Select(t => t.Value)).Trim())
                .ToList())
            .ToList();
        return FormatMarkdownTable(rows);
    }

    // ── XLSX (Excel) ──────────────────────────────────────────────────────────

    static readonly XNamespace XL = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string ExtractXlsx(ZipArchive zip)
    {
        var sharedStrings = LoadXlsxSharedStrings(zip);
        var sheetNames    = LoadXlsxSheetNames(zip);

        var sheetEntries = zip.Entries
            .Where(e => Regex.IsMatch(e.FullName,
                @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(e => e.FullName)
            .ToList();

        if (sheetEntries.Count == 0) return "[No worksheets found in .xlsx]";

        var sb = new StringBuilder();
        for (int i = 0; i < sheetEntries.Count; i++)
        {
            var name = sheetNames.TryGetValue(i + 1, out var n) ? n : $"Sheet{i + 1}";
            sb.AppendLine($"## {name}");
            sb.AppendLine();
            sb.AppendLine(ExtractXlsxSheet(sheetEntries[i], sharedStrings));
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    private static List<string> LoadXlsxSharedStrings(ZipArchive zip)
    {
        var list  = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return list;

        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        foreach (var si in doc.Root?.Elements(XL + "si") ?? [])
            list.Add(string.Concat(si.Descendants(XL + "t").Select(t => t.Value)));

        return list;
    }

    private static Dictionary<int, string> LoadXlsxSheetNames(ZipArchive zip)
    {
        var map   = new Dictionary<int, string>();
        var entry = zip.GetEntry("xl/workbook.xml");
        if (entry is null) return map;

        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        int i = 1;
        foreach (var sheet in doc.Descendants(XL + "sheet"))
            map[i++] = sheet.Attribute("name")?.Value ?? $"Sheet{i}";

        return map;
    }

    private static string ExtractXlsxSheet(ZipArchiveEntry sheetEntry, List<string> sharedStrings)
    {
        XDocument doc;
        using (var s = sheetEntry.Open()) doc = XDocument.Load(s);

        // Build sparse row-index → col-index → value map
        var rowMap = new SortedDictionary<int, SortedDictionary<int, string>>();

        foreach (var rowEl in doc.Descendants(XL + "row"))
        {
            int rowIdx = int.TryParse(rowEl.Attribute("r")?.Value, out var ri) ? ri : 0;
            var cols   = new SortedDictionary<int, string>();

            foreach (var cell in rowEl.Elements(XL + "c"))
            {
                int col  = XlsxColIndex(cell.Attribute("r")?.Value ?? "");
                if (col < 0) continue;

                var t   = cell.Attribute("t")?.Value;
                string val;

                if (t == "s" && int.TryParse(cell.Element(XL + "v")?.Value, out var si)
                             && (uint)si < (uint)sharedStrings.Count)
                    val = sharedStrings[si];
                else if (t == "inlineStr")
                    val = cell.Descendants(XL + "t").FirstOrDefault()?.Value ?? "";
                else
                    val = cell.Element(XL + "v")?.Value ?? "";

                if (!string.IsNullOrEmpty(val))
                    cols[col] = val;
            }

            if (cols.Count > 0)
                rowMap[rowIdx] = cols;
        }

        if (rowMap.Count == 0) return "*(empty sheet)*";

        int maxCol   = rowMap.Values.Max(r => r.Keys.Max());
        var tableData = rowMap.Values
            .Select(row => Enumerable.Range(0, maxCol + 1)
                .Select(c => row.TryGetValue(c, out var v) ? v : "")
                .ToList())
            .ToList();

        return FormatMarkdownTable(tableData);
    }

    /// <summary>Converts an Excel cell reference like "AB12" to a 0-based column index.</summary>
    private static int XlsxColIndex(string cellRef)
    {
        int col = 0;
        foreach (var c in cellRef.TakeWhile(char.IsLetter))
            col = col * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return col - 1;   // 0-based; returns -1 when cellRef has no letters
    }

    // ── PPTX (PowerPoint) ─────────────────────────────────────────────────────

    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static string ExtractPptx(ZipArchive zip)
    {
        var slides = zip.Entries
            .Where(e => Regex.IsMatch(e.FullName,
                @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(e => e.FullName)
            .ToList();

        if (slides.Count == 0) return "[No slides found in .pptx]";

        var sb = new StringBuilder();
        for (int i = 0; i < slides.Count; i++)
        {
            sb.AppendLine($"## Slide {i + 1}");
            sb.AppendLine();

            XDocument doc;
            using (var s = slides[i].Open()) doc = XDocument.Load(s);

            bool hadText = false;
            foreach (var txBody in doc.Descendants(A + "txBody"))
            {
                foreach (var para in txBody.Elements(A + "p"))
                {
                    var text = string.Concat(para.Descendants(A + "t").Select(t => t.Value));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(text);
                        hadText = true;
                    }
                }
            }

            if (!hadText) sb.AppendLine("*(no text on this slide)*");
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    // ── ODF common (ODT / ODS / ODP) ──────────────────────────────────────────

    private enum OdfMode { Text, Spreadsheet, Presentation }

    static readonly XNamespace OdfOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    static readonly XNamespace OdfText   = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    static readonly XNamespace OdfTable  = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    static readonly XNamespace OdfDraw   = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";

    private static string ExtractOdf(ZipArchive zip, OdfMode mode)
    {
        var entry = zip.GetEntry("content.xml");
        if (entry is null) return "[content.xml not found in ODF archive]";

        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        return mode switch
        {
            OdfMode.Text         => ExtractOdtContent(doc),
            OdfMode.Spreadsheet  => ExtractOdsContent(doc),
            OdfMode.Presentation => ExtractOdpContent(doc),
            _                    => ""
        };
    }

    // ── ODT (Writer) ──────────────────────────────────────────────────────────

    private static string ExtractOdtContent(XDocument doc)
    {
        // office:body > office:text
        var textRoot = doc.Descendants(OdfOffice + "text").FirstOrDefault();
        if (textRoot is null) return "[Empty document]";

        var sb = new StringBuilder();
        WalkOdtNode(textRoot, sb);
        return sb.ToString().Trim();
    }

    private static void WalkOdtNode(XElement el, StringBuilder sb)
    {
        foreach (var child in el.Elements())
        {
            if (child.Name == OdfText + "p" || child.Name == OdfText + "h")
            {
                sb.AppendLine(GetOdfParagraphText(child));
            }
            else if (child.Name == OdfText + "list")
            {
                ExtractOdfList(child, sb, depth: 0);
            }
            else if (child.Name == OdfTable + "table")
            {
                sb.AppendLine();
                sb.AppendLine(ExtractOdfTable(child));
                sb.AppendLine();
            }
            else
            {
                // Section, frame, annotation, etc. — recurse
                WalkOdtNode(child, sb);
            }
        }
    }

    private static void ExtractOdfList(XElement listEl, StringBuilder sb, int depth)
    {
        foreach (var item in listEl.Elements(OdfText + "list-item"))
        {
            foreach (var para in item.Elements(OdfText + "p"))
            {
                var text = GetOdfParagraphText(para);
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine($"{new string(' ', depth * 2)}- {text}");
            }
            // Nested list
            foreach (var nested in item.Elements(OdfText + "list"))
                ExtractOdfList(nested, sb, depth + 1);
        }
    }

    // ── ODS (Calc) ────────────────────────────────────────────────────────────

    private static string ExtractOdsContent(XDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var table in doc.Descendants(OdfTable + "table"))
        {
            var name = table.Attribute(OdfTable + "name")?.Value ?? "Sheet";
            sb.AppendLine($"## {name}");
            sb.AppendLine();
            sb.AppendLine(ExtractOdfTable(table));
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    // ── ODP (Impress) ─────────────────────────────────────────────────────────

    private static string ExtractOdpContent(XDocument doc)
    {
        var pages = doc.Descendants(OdfDraw + "page").ToList();
        if (pages.Count == 0) return "[No slides found in .odp]";

        var sb = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            sb.AppendLine($"## Slide {i + 1}");
            sb.AppendLine();

            bool hadText = false;
            foreach (var para in pages[i].Descendants(OdfText + "p"))
            {
                var text = GetOdfParagraphText(para);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                    hadText = true;
                }
            }

            if (!hadText) sb.AppendLine("*(no text on this slide)*");
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    // ── ODF table extractor (shared by ODT and ODS) ───────────────────────────

    private static string ExtractOdfTable(XElement table)
    {
        const int MaxCols = 100;   // guard against huge number-columns-repeated values
        var tableData = new List<List<string>>();

        foreach (var rowEl in table.Elements(OdfTable + "table-row"))
        {
            // Skip mass-repeated blank rows (common ODS padding at bottom of sheet)
            var rowRepeat = int.TryParse(
                rowEl.Attribute(OdfTable + "number-rows-repeated")?.Value, out var rr) ? rr : 1;
            if (rowRepeat > 50) continue;

            var cells = new List<string>();

            // Both normal and merged-covered cells
            foreach (var cell in rowEl.Elements(OdfTable + "table-cell")
                .Concat(rowEl.Elements(OdfTable + "covered-table-cell")))
            {
                var colRepeat = int.TryParse(
                    cell.Attribute(OdfTable + "number-columns-repeated")?.Value, out var cr) ? cr : 1;

                // Concatenate all text:p paragraphs inside this cell
                var text = string.Join(" ", cell.Elements(OdfText + "p")
                    .Select(p => GetOdfParagraphText(p).Trim())
                    .Where(t => t.Length > 0));

                int addCount = Math.Min(colRepeat, MaxCols - cells.Count);
                for (int i = 0; i < addCount; i++)
                    cells.Add(text);

                if (cells.Count >= MaxCols) break;
            }

            // Trim trailing empty cells (ODS pads rows to full sheet width)
            while (cells.Count > 0 && string.IsNullOrEmpty(cells[^1]))
                cells.RemoveAt(cells.Count - 1);

            if (cells.Count > 0)
                for (int r = 0; r < rowRepeat; r++)
                    tableData.Add(new List<string>(cells));
        }

        return tableData.Count == 0 ? "*(empty table)*" : FormatMarkdownTable(tableData);
    }

    // ── ODF paragraph text helper ─────────────────────────────────────────────

    /// <summary>
    /// Extracts plain text from a text:p or text:h element.
    /// Handles ODF whitespace elements (text:s, text:tab, text:line-break).
    /// Skips text that is nested inside a table within the paragraph (avoids double-output).
    /// </summary>
    private static string GetOdfParagraphText(XElement para)
    {
        var sb = new StringBuilder();

        foreach (var node in para.DescendantNodes())
        {
            // Skip anything inside a nested table
            bool inTable = false;
            for (var p = (node is XElement ne ? ne.Parent : (node as XText)?.Parent); p != null && p != para; p = p.Parent)
            {
                if (p.Name == OdfTable + "table") { inTable = true; break; }
            }
            if (inTable) continue;

            if (node is XText textNode)
            {
                sb.Append(textNode.Value);
            }
            else if (node is XElement el)
            {
                // ODF whitespace elements
                if (el.Name == OdfText + "s")
                {
                    int count = int.TryParse(el.Attribute(OdfText + "c")?.Value, out var n) ? n : 1;
                    sb.Append(new string(' ', count));
                }
                else if (el.Name == OdfText + "tab")
                {
                    sb.Append('\t');
                }
                else if (el.Name == OdfText + "line-break")
                {
                    sb.Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    // ── Markdown table formatter ──────────────────────────────────────────────

    /// <summary>
    /// Formats a list of rows as a Markdown table.
    /// The first row is treated as the header; a separator row is inserted below it.
    /// All columns are padded to their maximum content width (minimum 3 for the separator dashes).
    /// </summary>
    private static string FormatMarkdownTable(List<List<string>> rows)
    {
        if (rows.Count == 0) return "";

        int cols = rows.Max(r => r.Count);
        // Normalize all rows to the same column count
        foreach (var r in rows)
            while (r.Count < cols) r.Add("");

        // Column widths: max content width, minimum 3 for "---"
        var widths = Enumerable.Range(0, cols)
            .Select(c => Math.Max(3, rows.Max(r => r[c].Length)))
            .ToArray();

        var sb = new StringBuilder();

        // Header row
        sb.Append('|');
        for (int c = 0; c < cols; c++)
            sb.Append($" {rows[0][c].PadRight(widths[c])} |");
        sb.AppendLine();

        // Separator
        sb.Append('|');
        for (int c = 0; c < cols; c++)
            sb.Append($" {new string('-', widths[c])} |");
        sb.AppendLine();

        // Data rows
        for (int r = 1; r < rows.Count; r++)
        {
            sb.Append('|');
            for (int c = 0; c < cols; c++)
                sb.Append($" {rows[r][c].PadRight(widths[c])} |");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
