using System.IO;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ClaudetRelay.Services;

/// <summary>
/// Extracts readable plain-text from PDF files using PdfPig.
///
/// Text is extracted page by page with best-effort reading-order reconstruction:
/// words are grouped by their vertical position into lines, then sorted
/// left-to-right within each line.  This handles most single-column PDFs well.
/// Complex multi-column layouts may still come out scrambled — that is a
/// fundamental limitation of the PDF format itself.
///
/// Scanned/image-only PDFs produce no text (OCR is out of scope).
/// </summary>
public static class PdfFileReader
{
    /// <summary>Returns true if the file has a .pdf extension.</summary>
    public static bool IsSupported(string filePath)
        => Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts human-readable text from a PDF.
    /// Returns null if the format is not supported.
    /// Returns a user-friendly error string (never throws) on failure.
    /// </summary>
    public static string? TryExtractText(string filePath)
    {
        if (!IsSupported(filePath)) return null;

        try
        {
            using var document = PdfDocument.Open(filePath);
            var sb   = new StringBuilder();
            int total = document.NumberOfPages;

            for (int i = 1; i <= total; i++)
            {
                var page = document.GetPage(i);

                if (i > 1) sb.AppendLine();
                if (total > 1)
                {
                    sb.AppendLine($"## Page {i}");
                    sb.AppendLine();
                }

                var text = ExtractPageText(page);
                if (string.IsNullOrWhiteSpace(text))
                    sb.AppendLine("*(no text on this page — may be image-only)*");
                else
                    sb.AppendLine(text);
            }

            var result = sb.ToString().Trim();
            return result.Length > 0 ? result : "[PDF contained no extractable text]";
        }
        catch (Exception ex)
        {
            return $"[Could not read PDF: {ex.Message}]";
        }
    }

    // ── Reading-order text reconstruction ────────────────────────────────────

    private static string ExtractPageText(Page page)
    {
        try
        {
            // Use NearestNeighbour word extractor which groups letters into words
            var words = NearestNeighbourWordExtractor.Instance
                .GetWords(page.Letters)
                .ToList();

            if (words.Count == 0)
                return page.Text?.Trim() ?? "";

            return ReconstructReadingOrder(words);
        }
        catch
        {
            // Fall back to the raw page text if word extraction fails
            return page.Text?.Trim() ?? "";
        }
    }

    /// <summary>
    /// Groups words into visual lines by Y position, then sorts each line
    /// left-to-right and joins everything into readable paragraphs.
    /// </summary>
    private static string ReconstructReadingOrder(IReadOnlyList<Word> words)
    {
        // PDF Y-axis: origin at bottom-left, increases upward.
        // Sort descending so the top of the page comes first.
        const double LineThreshold = 3.0;   // pts — words within this range share a line

        var lines = new List<List<Word>>();

        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            bool placed = false;
            foreach (var line in lines)
            {
                if (Math.Abs(word.BoundingBox.Bottom - line[0].BoundingBox.Bottom) <= LineThreshold)
                {
                    line.Add(word);
                    placed = true;
                    break;
                }
            }
            if (!placed) lines.Add(new List<Word> { word });
        }

        // Within each line: left-to-right order
        var sb = new StringBuilder();
        double prevLineBottom = double.MaxValue;

        foreach (var line in lines)
        {
            double lineBottom = line[0].BoundingBox.Bottom;

            // Insert blank line when there is a large vertical gap (paragraph break)
            if (prevLineBottom - lineBottom > 14)
                sb.AppendLine();

            var lineText = string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
            sb.AppendLine(lineText);
            prevLineBottom = lineBottom;
        }

        return sb.ToString().Trim();
    }
}
