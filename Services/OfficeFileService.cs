using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClaudetRelay.Services;

/// <summary>
/// Reads and writes modern Office and OpenDocument files.
/// All formats are ZIP-based — no external NuGet packages required.
///
/// READING  (.docx .xlsx .pptx .odt .ods .odp)
///   Text is extracted and returned as plain text / Markdown tables.
///   Call <see cref="IsSupported"/> then <see cref="TryExtractText"/>.
///
/// WRITING  (.docx .odt .xlsx .ods)
///   Markdown content is converted to the target format.
///   Call <see cref="CanWrite"/> then <see cref="TryWrite"/>.
///
/// Legacy binary formats (.doc .xls .ppt) are NOT supported.
/// </summary>
public static class OfficeFileService
{
    // ══════════════════════════════════════════════════════════════════════════
    //  WRITING
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly HashSet<string> _writable = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".odt", ".xlsx", ".ods" };

    /// <summary>Returns true if the app can generate this file format from Markdown.</summary>
    public static bool CanWrite(string filePath)
        => _writable.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// Converts <paramref name="markdown"/> to the Office/ODF format implied by
    /// the file extension and saves the result to <paramref name="filePath"/>.
    /// Returns false and sets <paramref name="error"/> on failure.
    /// </summary>
    public static bool TryWrite(string filePath, string markdown, out string? error)
    {
        error = null;
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            switch (ext)
            {
                case ".docx": WriteDocx(filePath, markdown); break;
                case ".odt":  WriteOdt (filePath, markdown); break;
                case ".xlsx": WriteXlsx(filePath, markdown); break;
                case ".ods":  WriteOds (filePath, markdown); break;
                default:
                    error = $"Cannot write format '{ext}'";
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── DOCX ──────────────────────────────────────────────────────────────────

    private static void WriteDocx(string filePath, string markdown)
    {
        using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);

        WriteZipEntry(zip, "[Content_Types].xml", DocxContentTypes());
        WriteZipEntry(zip, "_rels/.rels",          DocxRootRels());
        WriteZipEntry(zip, "word/_rels/document.xml.rels", DocxDocumentRels());
        WriteZipEntry(zip, "word/styles.xml",      DocxStyles());
        WriteZipEntry(zip, "word/document.xml",    DocxDocument(markdown));
    }

    private static string DocxContentTypes() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\"  ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\"   ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "</Types>";

    private static string DocxRootRels() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";

    private static string DocxDocumentRels() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    private static string DocxStyles() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        // Normal
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/>" +
        "<w:rPr><w:sz w:val=\"22\"/></w:rPr></w:style>" +
        // Heading 1–3
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/>" +
        "<w:pPr><w:outlineLvl w:val=\"0\"/><w:spacing w:before=\"240\" w:after=\"60\"/></w:pPr>" +
        "<w:rPr><w:b/><w:sz w:val=\"48\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/>" +
        "<w:pPr><w:outlineLvl w:val=\"1\"/><w:spacing w:before=\"200\" w:after=\"60\"/></w:pPr>" +
        "<w:rPr><w:b/><w:sz w:val=\"36\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading3\"><w:name w:val=\"heading 3\"/>" +
        "<w:pPr><w:outlineLvl w:val=\"2\"/><w:spacing w:before=\"160\" w:after=\"40\"/></w:pPr>" +
        "<w:rPr><w:b/><w:sz w:val=\"28\"/></w:rPr></w:style>" +
        // Code block
        "<w:style w:type=\"paragraph\" w:styleId=\"CodeBlock\"><w:name w:val=\"Code Block\"/>" +
        "<w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F5F5F5\"/>" +
        "<w:ind w:left=\"360\" w:right=\"360\"/></w:pPr>" +
        "<w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\"/><w:sz w:val=\"18\"/></w:rPr></w:style>" +
        "</w:styles>";

    private static string DocxDocument(string markdown)
    {
        const string NS = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        var body = BuildDocxBody(markdown);
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
               $"<w:document {NS}><w:body>{body}" +
               // Section properties (page margins)
               "<w:sectPr><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\"/></w:sectPr>" +
               "</w:body></w:document>";
    }

    private static string BuildDocxBody(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var sb    = new StringBuilder();
        int i     = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Code block
            if (line.TrimStart().StartsWith("```"))
            {
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    sb.Append(DocxCodePara(lines[i++]));
                i++;  // closing ```
                continue;
            }

            // HR
            if (Regex.IsMatch(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                sb.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"AAAAAA\"/></w:pBdr></w:pPr></w:p>");
                i++; continue;
            }

            // Table
            if (line.TrimStart().StartsWith('|'))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                    rows.Add(lines[i++]);
                sb.Append(DocxTable(rows));
                continue;
            }

            // Headings
            if (line.StartsWith("### ")) { sb.Append(DocxHeadingPara(line[4..], "Heading3")); i++; continue; }
            if (line.StartsWith("## "))  { sb.Append(DocxHeadingPara(line[3..], "Heading2")); i++; continue; }
            if (line.StartsWith("# "))   { sb.Append(DocxHeadingPara(line[2..], "Heading1")); i++; continue; }

            // List items
            var lm = Regex.Match(line, @"^(\s*)([-*]|\d+\.)\s+(.*)");
            if (lm.Success)
            {
                int depth = lm.Groups[1].Length / 2;
                int indPt = 360 + depth * 360;
                sb.Append($"<w:p><w:pPr><w:ind w:left=\"{indPt}\"/></w:pPr><w:r><w:t xml:space=\"preserve\">• </w:t></w:r>{DocxRuns(lm.Groups[3].Value)}</w:p>");
                i++; continue;
            }

            // Blank line
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append("<w:p><w:pPr><w:spacing w:after=\"80\"/></w:pPr></w:p>");
                i++; continue;
            }

            // Normal paragraph
            sb.Append($"<w:p>{DocxRuns(line)}</w:p>");
            i++;
        }

        return sb.ToString();
    }

    private static string DocxHeadingPara(string text, string styleId) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"{styleId}\"/></w:pPr>{DocxRuns(text)}</w:p>";

    private static string DocxCodePara(string text) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"CodeBlock\"/></w:pPr>" +
        $"<w:r><w:t xml:space=\"preserve\">{XmlEncode(text)}</w:t></w:r></w:p>";

    private static string DocxTable(List<string> tableLines)
    {
        var dataRows = tableLines
            .Where(l => !Regex.IsMatch(l, @"^\s*\|[\s\-|:]+\|\s*$"))
            .Select(l => l.Trim('|', ' ').Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        if (dataRows.Count == 0) return "";

        int cols = dataRows.Max(r => r.Length);
        int colW = 9360 / cols;  // total page width 9360 twips ≈ 16.5 cm

        var sb = new StringBuilder();
        sb.Append("<w:tbl><w:tblPr>" +
                  "<w:tblStyle w:val=\"TableGrid\"/>" +
                  $"<w:tblW w:w=\"{cols * colW}\" w:type=\"dxa\"/>" +
                  "<w:tblBorders>" +
                  "<w:top    w:val=\"single\" w:sz=\"4\" w:color=\"AAAAAA\"/>" +
                  "<w:left   w:val=\"single\" w:sz=\"4\" w:color=\"AAAAAA\"/>" +
                  "<w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"AAAAAA\"/>" +
                  "<w:right  w:val=\"single\" w:sz=\"4\" w:color=\"AAAAAA\"/>" +
                  "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"DDDDDD\"/>" +
                  "<w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"DDDDDD\"/>" +
                  "</w:tblBorders></w:tblPr>" +
                  $"<w:tblGrid>{string.Concat(Enumerable.Repeat($"<w:gridCol w:w=\"{colW}\"/>", cols))}</w:tblGrid>");

        for (int r = 0; r < dataRows.Count; r++)
        {
            bool isHeader = r == 0;
            sb.Append("<w:tr>");
            if (isHeader) sb.Append("<w:trPr><w:tblHeader/></w:trPr>");

            for (int c = 0; c < cols; c++)
            {
                var cellText = c < dataRows[r].Length ? dataRows[r][c] : "";
                string shade = isHeader ? "<w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"E8E8E8\"/></w:tcPr>" : "";
                string boldWrap = isHeader ? $"<w:rPr><w:b/></w:rPr>" : "";
                sb.Append($"<w:tc>{shade}<w:p><w:r>{boldWrap}<w:t xml:space=\"preserve\">{XmlEncode(cellText)}</w:t></w:r></w:p></w:tc>");
            }
            sb.Append("</w:tr>");
        }

        sb.Append("</w:tbl><w:p/>"); // empty paragraph after table is required by Word
        return sb.ToString();
    }

    private static string DocxRuns(string text)
    {
        var sb = new StringBuilder();
        foreach (var (t, bold, italic, code) in ParseInlineRuns(text))
        {
            if (string.IsNullOrEmpty(t)) continue;
            sb.Append("<w:r>");
            if (bold || italic || code)
            {
                sb.Append("<w:rPr>");
                if (bold)   sb.Append("<w:b/>");
                if (italic) sb.Append("<w:i/>");
                if (code)   sb.Append("<w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\"/><w:sz w:val=\"18\"/>");
                sb.Append("</w:rPr>");
            }
            sb.Append($"<w:t xml:space=\"preserve\">{XmlEncode(t)}</w:t></w:r>");
        }
        return sb.ToString();
    }

    // ── ODT (Writer) ──────────────────────────────────────────────────────────

    private static void WriteOdt(string filePath, string markdown)
    {
        using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);

        // mimetype MUST be first and uncompressed per ODF spec
        WriteZipEntry(zip, "mimetype",
            "application/vnd.oasis.opendocument.text",
            CompressionLevel.NoCompression);

        WriteZipEntry(zip, "META-INF/manifest.xml", OdtManifest("text"));
        WriteZipEntry(zip, "styles.xml",             OdtStyles("text"));
        WriteZipEntry(zip, "content.xml",            OdtDocument(markdown));
    }

    private static string OdtManifest(string docType) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">" +
        $"<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.{docType}\"/>" +
        "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
        "<manifest:file-entry manifest:full-path=\"styles.xml\"  manifest:media-type=\"text/xml\"/>" +
        "</manifest:manifest>";

    private static string OdtStyles(string _ /*docType*/) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<office:document-styles " +
        "  xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"" +
        "  xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\"" +
        "  xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"" +
        "  office:version=\"1.3\">" +
        "<office:styles>" +
        "<style:style style:name=\"Text_Body\" style:family=\"paragraph\">" +
        "  <style:paragraph-properties fo:margin-bottom=\"0.2cm\"/>" +
        "  <style:text-properties fo:font-size=\"11pt\"/></style:style>" +
        "<style:style style:name=\"Heading_1\" style:family=\"paragraph\">" +
        "  <style:paragraph-properties fo:margin-top=\"0.5cm\" fo:margin-bottom=\"0.2cm\"/>" +
        "  <style:text-properties fo:font-size=\"18pt\" fo:font-weight=\"bold\"/></style:style>" +
        "<style:style style:name=\"Heading_2\" style:family=\"paragraph\">" +
        "  <style:paragraph-properties fo:margin-top=\"0.4cm\" fo:margin-bottom=\"0.15cm\"/>" +
        "  <style:text-properties fo:font-size=\"14pt\" fo:font-weight=\"bold\"/></style:style>" +
        "<style:style style:name=\"Heading_3\" style:family=\"paragraph\">" +
        "  <style:paragraph-properties fo:margin-top=\"0.3cm\" fo:margin-bottom=\"0.1cm\"/>" +
        "  <style:text-properties fo:font-size=\"12pt\" fo:font-weight=\"bold\"/></style:style>" +
        "<style:style style:name=\"Code_Block\" style:family=\"paragraph\">" +
        "  <style:paragraph-properties fo:background-color=\"#F5F5F5\" fo:padding=\"0.2cm\" fo:margin=\"0.3cm 0cm\"/>" +
        "  <style:text-properties fo:font-name=\"Courier New\" fo:font-size=\"10pt\"/></style:style>" +
        "<style:style style:name=\"List_Item\" style:family=\"paragraph\">" +
        "  <style:paragraph-properties fo:margin-left=\"0.75cm\" fo:margin-bottom=\"0.1cm\"/>" +
        "  <style:text-properties fo:font-size=\"11pt\"/></style:style>" +
        "<style:style style:name=\"Bold\" style:family=\"text\">" +
        "  <style:text-properties fo:font-weight=\"bold\"/></style:style>" +
        "<style:style style:name=\"Italic\" style:family=\"text\">" +
        "  <style:text-properties fo:font-style=\"italic\"/></style:style>" +
        "<style:style style:name=\"Code_Span\" style:family=\"text\">" +
        "  <style:text-properties fo:font-name=\"Courier New\" fo:font-size=\"10pt\"/></style:style>" +
        "</office:styles></office:document-styles>";

    private static string OdtDocument(string markdown)
    {
        const string NS =
            "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
            "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
            "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
            "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
            "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
            "office:version=\"1.3\"";

        var body = BuildOdtBody(markdown);
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               $"<office:document-content {NS}>" +
               $"<office:body><office:text>{body}</office:text></office:body>" +
               $"</office:document-content>";
    }

    private static string BuildOdtBody(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var sb    = new StringBuilder();
        int i     = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Code block
            if (line.TrimStart().StartsWith("```"))
            {
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    sb.Append($"<text:p text:style-name=\"Code_Block\">{XmlEncode(lines[i++])}</text:p>");
                i++;
                continue;
            }

            // HR
            if (Regex.IsMatch(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                sb.Append("<text:p text:style-name=\"Text_Body\"><text:line-break/></text:p>");
                i++; continue;
            }

            // Table
            if (line.TrimStart().StartsWith('|'))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                    rows.Add(lines[i++]);
                sb.Append(OdtTable(rows));
                continue;
            }

            // Headings
            if (line.StartsWith("### ")) { sb.Append(OdtPara(line[4..], "Heading_3")); i++; continue; }
            if (line.StartsWith("## "))  { sb.Append(OdtPara(line[3..], "Heading_2")); i++; continue; }
            if (line.StartsWith("# "))   { sb.Append(OdtPara(line[2..], "Heading_1")); i++; continue; }

            // List item
            var lm = Regex.Match(line, @"^(\s*)([-*]|\d+\.)\s+(.*)");
            if (lm.Success)
            {
                sb.Append($"<text:p text:style-name=\"List_Item\">• {OdtSpans(lm.Groups[3].Value)}</text:p>");
                i++; continue;
            }

            // Blank
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append("<text:p text:style-name=\"Text_Body\"/>");
                i++; continue;
            }

            // Normal
            sb.Append(OdtPara(line, "Text_Body"));
            i++;
        }

        return sb.ToString();
    }

    private static string OdtPara(string text, string style) =>
        $"<text:p text:style-name=\"{style}\">{OdtSpans(text)}</text:p>";

    private static string OdtSpans(string text)
    {
        var sb = new StringBuilder();
        foreach (var (t, bold, italic, code) in ParseInlineRuns(text))
        {
            if (string.IsNullOrEmpty(t)) continue;
            var styleName = (bold, italic, code) switch
            {
                (true,  false, false) => "Bold",
                (false, true,  false) => "Italic",
                (_,     _,     true ) => "Code_Span",
                _                    => null
            };
            var escaped = XmlEncode(t);
            sb.Append(styleName is null
                ? escaped
                : $"<text:span text:style-name=\"{styleName}\">{escaped}</text:span>");
        }
        return sb.ToString();
    }

    private static string OdtTable(List<string> tableLines)
    {
        var dataRows = tableLines
            .Where(l => !Regex.IsMatch(l, @"^\s*\|[\s\-|:]+\|\s*$"))
            .Select(l => l.Trim('|', ' ').Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        if (dataRows.Count == 0) return "";

        int cols = dataRows.Max(r => r.Length);
        var sb   = new StringBuilder();
        sb.Append("<table:table table:name=\"Table1\">");
        for (int c = 0; c < cols; c++)
            sb.Append("<table:table-column/>");

        for (int r = 0; r < dataRows.Count; r++)
        {
            sb.Append("<table:table-row>");
            for (int c = 0; c < cols; c++)
            {
                var cell = c < dataRows[r].Length ? dataRows[r][c] : "";
                var text = r == 0
                    ? $"<text:span text:style-name=\"Bold\">{XmlEncode(cell)}</text:span>"
                    : XmlEncode(cell);
                sb.Append($"<table:table-cell><text:p>{text}</text:p></table:table-cell>");
            }
            sb.Append("</table:table-row>");
        }
        sb.Append("</table:table>");
        return sb.ToString();
    }

    // ── XLSX (Excel) ──────────────────────────────────────────────────────────

    private static void WriteXlsx(string filePath, string markdown)
    {
        var sheets = ParseMarkdownToSheets(markdown);
        if (sheets.Count == 0) sheets.Add(("Sheet1", new List<string[]> { new[] { markdown } }));

        using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);

        var sheetRels = new StringBuilder();
        var sheetOverrides = new StringBuilder();
        for (int s = 0; s < sheets.Count; s++)
        {
            int id = s + 1;
            sheetRels.Append($"<Relationship Id=\"rId{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{id}.xml\"/>");
            sheetOverrides.Append($"<Override PartName=\"/xl/worksheets/sheet{id}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            WriteZipEntry(zip, $"xl/worksheets/sheet{id}.xml", XlsxSheet(sheets[s].Rows));
        }

        WriteZipEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            sheetOverrides +
            "</Types>");

        WriteZipEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        var sheetElems = new StringBuilder();
        for (int s = 0; s < sheets.Count; s++)
            sheetElems.Append($"<sheet name=\"{XmlEncode(sheets[s].Name)}\" sheetId=\"{s + 1}\" r:id=\"rId{s + 1}\"/>");

        WriteZipEntry(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "          xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            $"<sheets>{sheetElems}</sheets></workbook>");

        WriteZipEntry(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            sheetRels +
            "</Relationships>");
    }

    private static string XlsxSheet(List<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Length; c++)
            {
                var addr = $"{ColLetter(c)}{r + 1}";
                sb.Append($"<c r=\"{addr}\" t=\"inlineStr\"><is><t>{XmlEncode(rows[r][c])}</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    // ── ODS (Calc) ────────────────────────────────────────────────────────────

    private static void WriteOds(string filePath, string markdown)
    {
        var sheets = ParseMarkdownToSheets(markdown);
        if (sheets.Count == 0) sheets.Add(("Sheet1", new List<string[]> { new[] { markdown } }));

        using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);

        WriteZipEntry(zip, "mimetype",
            "application/vnd.oasis.opendocument.spreadsheet",
            CompressionLevel.NoCompression);

        WriteZipEntry(zip, "META-INF/manifest.xml", OdtManifest("spreadsheet"));
        WriteZipEntry(zip, "styles.xml",  "<office:document-styles xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"/>");

        var tableNS =
            "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
            "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
            "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
            "office:version=\"1.3\"";

        var tablesXml = new StringBuilder();
        foreach (var (name, rows) in sheets)
        {
            tablesXml.Append($"<table:table table:name=\"{XmlEncode(name)}\">");
            foreach (var row in rows)
            {
                tablesXml.Append("<table:table-row>");
                foreach (var cell in row)
                    tablesXml.Append($"<table:table-cell><text:p>{XmlEncode(cell)}</text:p></table:table-cell>");
                tablesXml.Append("</table:table-row>");
            }
            tablesXml.Append("</table:table>");
        }

        WriteZipEntry(zip, "content.xml",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<office:document-content {tableNS}>" +
            $"<office:body><office:spreadsheet>{tablesXml}</office:spreadsheet></office:body>" +
            $"</office:document-content>");
    }

    // ── Shared write helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Parses all Markdown tables from <paramref name="markdown"/> into
    /// named sheets.  Each table becomes one sheet named after the last
    /// heading that preceded it, or "Sheet N" if none.
    /// Remaining non-table content is ignored for spreadsheet output.
    /// </summary>
    private static List<(string Name, List<string[]> Rows)> ParseMarkdownToSheets(string markdown)
    {
        var sheets = new List<(string Name, List<string[]> Rows)>();
        var lines  = markdown.ReplaceLineEndings("\n").Split('\n');
        var lastHeading = "Sheet1";
        int sheetNum    = 0;

        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.StartsWith("# ")  || line.StartsWith("## ") || line.StartsWith("### "))
            {
                lastHeading = Regex.Replace(line, @"^#+\s+", "").Trim();
                i++; continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                    tableLines.Add(lines[i++]);

                var rows = tableLines
                    .Where(l => !Regex.IsMatch(l, @"^\s*\|[\s\-|:]+\|\s*$"))
                    .Select(l => l.Trim('|', ' ').Split('|').Select(c => c.Trim()).ToArray())
                    .ToList();

                if (rows.Count > 0)
                {
                    sheetNum++;
                    var name = sheetNum == 1 ? lastHeading : $"{lastHeading} {sheetNum}";
                    sheets.Add((name, rows));
                }
                continue;
            }

            i++;
        }

        return sheets;
    }

    /// <summary>Parses inline Markdown runs: bold, italic, code spans.</summary>
    private static IEnumerable<(string Text, bool Bold, bool Italic, bool Code)>
        ParseInlineRuns(string text)
    {
        var result = new List<(string, bool, bool, bool)>();
        int pos    = 0;
        var cur    = new StringBuilder();
        bool bold = false, italic = false, code = false;

        void Flush()
        {
            if (cur.Length > 0)
            {
                result.Add((cur.ToString(), bold, italic, code));
                cur.Clear();
            }
        }

        while (pos < text.Length)
        {
            // Code span
            if (text[pos] == '`')
            {
                Flush(); code = !code; pos++; continue;
            }
            // Bold **
            if (pos + 1 < text.Length && text[pos] == '*' && text[pos + 1] == '*')
            {
                Flush(); bold = !bold; pos += 2; continue;
            }
            // Italic *
            if (text[pos] == '*')
            {
                Flush(); italic = !italic; pos++; continue;
            }
            // Strikethrough ~~ (just strip)
            if (pos + 1 < text.Length && text[pos] == '~' && text[pos + 1] == '~')
            {
                pos += 2; continue;
            }

            cur.Append(text[pos++]);
        }
        Flush();
        return result;
    }

    private static string XmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string ColLetter(int col)   // 0-based → "A", "B", … "AA"
    {
        var s = string.Empty;
        for (col++; col > 0; col = (col - 1) / 26)
            s = (char)('A' + (col - 1) % 26) + s;
        return s;
    }

    private static void WriteZipEntry(ZipArchive zip, string path, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(path, level);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  READING
    // ══════════════════════════════════════════════════════════════════════════

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

    // ── DOCX ──────────────────────────────────────────────────────────────────

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
                sb.AppendLine(string.Concat(el.Descendants(W + "t").Select(t => t.Value)));
            else if (el.Name == W + "tbl")
            {
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

    // ── XLSX ──────────────────────────────────────────────────────────────────

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

        int maxCol    = rowMap.Values.Max(r => r.Keys.Max());
        var tableData = rowMap.Values
            .Select(row => Enumerable.Range(0, maxCol + 1)
                .Select(c => row.TryGetValue(c, out var v) ? v : "")
                .ToList())
            .ToList();

        return FormatMarkdownTable(tableData);
    }

    private static int XlsxColIndex(string cellRef)
    {
        int col = 0;
        foreach (var c in cellRef.TakeWhile(char.IsLetter))
            col = col * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return col - 1;
    }

    // ── PPTX ──────────────────────────────────────────────────────────────────

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

    // ── ODF common ────────────────────────────────────────────────────────────

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

    private static string ExtractOdtContent(XDocument doc)
    {
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
                sb.AppendLine(GetOdfParagraphText(child));
            else if (child.Name == OdfText + "list")
                ExtractOdfList(child, sb, 0);
            else if (child.Name == OdfTable + "table")
            {
                sb.AppendLine();
                sb.AppendLine(ExtractOdfTable(child));
                sb.AppendLine();
            }
            else
                WalkOdtNode(child, sb);
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
            foreach (var nested in item.Elements(OdfText + "list"))
                ExtractOdfList(nested, sb, depth + 1);
        }
    }

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

    private static string ExtractOdfTable(XElement table)
    {
        const int MaxCols = 100;
        var tableData = new List<List<string>>();

        foreach (var rowEl in table.Elements(OdfTable + "table-row"))
        {
            var rowRepeat = int.TryParse(
                rowEl.Attribute(OdfTable + "number-rows-repeated")?.Value, out var rr) ? rr : 1;
            if (rowRepeat > 50) continue;

            var cells = new List<string>();
            foreach (var cell in rowEl.Elements(OdfTable + "table-cell")
                .Concat(rowEl.Elements(OdfTable + "covered-table-cell")))
            {
                var colRepeat = int.TryParse(
                    cell.Attribute(OdfTable + "number-columns-repeated")?.Value, out var cr) ? cr : 1;

                var text = string.Join(" ", cell.Elements(OdfText + "p")
                    .Select(p => GetOdfParagraphText(p).Trim())
                    .Where(t => t.Length > 0));

                int addCount = Math.Min(colRepeat, MaxCols - cells.Count);
                for (int i = 0; i < addCount; i++) cells.Add(text);
                if (cells.Count >= MaxCols) break;
            }

            while (cells.Count > 0 && string.IsNullOrEmpty(cells[^1]))
                cells.RemoveAt(cells.Count - 1);

            if (cells.Count > 0)
                for (int r = 0; r < rowRepeat; r++)
                    tableData.Add(new List<string>(cells));
        }

        return tableData.Count == 0 ? "*(empty table)*" : FormatMarkdownTable(tableData);
    }

    private static string GetOdfParagraphText(XElement para)
    {
        var sb = new StringBuilder();
        foreach (var node in para.DescendantNodes())
        {
            bool inTable = false;
            for (var p = (node is XElement ne ? ne.Parent : (node as XText)?.Parent);
                 p != null && p != para; p = p.Parent)
            {
                if (p.Name == OdfTable + "table") { inTable = true; break; }
            }
            if (inTable) continue;

            if (node is XText textNode)
                sb.Append(textNode.Value);
            else if (node is XElement el)
            {
                if      (el.Name == OdfText + "s")
                    sb.Append(new string(' ', int.TryParse(el.Attribute(OdfText + "c")?.Value, out var n) ? n : 1));
                else if (el.Name == OdfText + "tab")
                    sb.Append('\t');
                else if (el.Name == OdfText + "line-break")
                    sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    // ── Markdown table formatter (shared by read and write paths) ─────────────

    private static string FormatMarkdownTable(List<List<string>> rows)
    {
        if (rows.Count == 0) return "";

        int cols = rows.Max(r => r.Count);
        foreach (var r in rows)
            while (r.Count < cols) r.Add("");

        var widths = Enumerable.Range(0, cols)
            .Select(c => Math.Max(3, rows.Max(r => r[c].Length)))
            .ToArray();

        var sb = new StringBuilder();
        sb.Append('|');
        for (int c = 0; c < cols; c++) sb.Append($" {rows[0][c].PadRight(widths[c])} |");
        sb.AppendLine();
        sb.Append('|');
        for (int c = 0; c < cols; c++) sb.Append($" {new string('-', widths[c])} |");
        sb.AppendLine();
        for (int r = 1; r < rows.Count; r++)
        {
            sb.Append('|');
            for (int c = 0; c < cols; c++) sb.Append($" {rows[r][c].PadRight(widths[c])} |");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
