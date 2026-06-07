using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudetRelay.Services;

/// <summary>
/// Handles web fetch requests from AI agents.
/// Validates domains against the active whitelist, fetches pages,
/// strips HTML to plain text, and manages file downloads into INPUT/Downloads/.
/// </summary>
public static class WebBrowsingService
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,   // we handle redirects manually to re-validate domains
    })
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClaudetRelay/1.0" }
        }
    };

    // ── Domain validation ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the given URL's host is covered by at least one entry in the whitelist.
    /// An entry "wikipedia.org" matches "en.wikipedia.org", "de.wikipedia.org", etc.
    /// </summary>
    public static bool IsDomainAllowed(string url, IEnumerable<WebWhitelistEntry> whitelist)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        foreach (var entry in whitelist)
        {
            if (!entry.IsEnabled) continue;
            var domain = entry.Domain.TrimStart('*', '.').ToLowerInvariant();
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Returns true if the whitelist entry for this URL has downloads enabled.</summary>
    public static bool IsDownloadAllowed(string url, IEnumerable<WebWhitelistEntry> whitelist)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        foreach (var entry in whitelist)
        {
            if (!entry.IsEnabled) continue;
            var domain = entry.Domain.TrimStart('*', '.').ToLowerInvariant();
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
                return entry.AllowDownloads;
        }
        return false;
    }

    // ── Web fetch ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch a URL and return plain text content.
    /// Handles redirects with re-validation, enforces timeout and char limits.
    /// Returns a <see cref="WebFetchResult"/> describing success or the failure reason.
    /// </summary>
    public static async Task<WebFetchResult> FetchAsync(
        string                       url,
        IEnumerable<WebWhitelistEntry> whitelist,
        int                          timeoutSeconds = 8,
        int                          maxChars       = 6000,
        CancellationToken            ct             = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return WebFetchResult.Fail(url, "Invalid URL format.");

        if (!IsDomainAllowed(url, whitelist))
            return WebFetchResult.Blocked(url);

        try
        {
            using var cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            // Follow redirects manually to re-validate each hop
            var currentUrl = url;
            for (int hop = 0; hop < 5; hop++)
            {
                using var request  = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // Redirect?
                if ((int)response.StatusCode is >= 301 and <= 308)
                {
                    var location = response.Headers.Location?.ToString();
                    if (string.IsNullOrEmpty(location))
                        return WebFetchResult.Fail(url, "Redirect with no Location header.");

                    // Resolve relative redirects
                    if (!Uri.IsWellFormedUriString(location, UriKind.Absolute))
                        location = new Uri(new Uri(currentUrl), location).ToString();

                    if (!IsDomainAllowed(location, whitelist))
                        return WebFetchResult.Blocked(location, $"Redirect to non-whitelisted domain: {new Uri(location).Host}");

                    currentUrl = location;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return WebFetchResult.Fail(url, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                // Non-text content type — offer as download instead
                if (!contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                    && !contentType.Contains("xml",  StringComparison.OrdinalIgnoreCase))
                {
                    return WebFetchResult.NonText(currentUrl, contentType);
                }

                var raw  = await response.Content.ReadAsStringAsync(cts.Token);
                var text = StripHtml(raw, out int imagesStripped);
                if (text.Length > maxChars)
                {
                    var imageReminder = imagesStripped > 0
                        ? $" — reminder: {imagesStripped} image{(imagesStripped == 1 ? " was" : "s were")} stripped from this page, image-only sections are empty in the text above"
                        : "";
                    text = text[..maxChars] + $"\n\n[Truncated — {text.Length:N0} chars total, showing first {maxChars:N0}{imageReminder}]";
                }

                return WebFetchResult.Ok(currentUrl, text, contentType, imagesStripped);
            }

            return WebFetchResult.Fail(url, "Too many redirects.");
        }
        catch (OperationCanceledException)
        {
            return WebFetchResult.Fail(url, $"Timed out after {timeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            return WebFetchResult.Fail(url, ex.Message);
        }
    }

    // ── File download ─────────────────────────────────────────────────────────

    /// <summary>
    /// Download a file into INPUT/Downloads/ inside the current project folder.
    /// Creates the Downloads subfolder if it does not exist.
    /// </summary>
    public static async Task<string> DownloadFileAsync(
        string            url,
        string            projectInputFolder,
        CancellationToken ct = default)
    {
        var downloadsFolder = Path.Combine(projectInputFolder, "Downloads");
        Directory.CreateDirectory(downloadsFolder);

        var fileName    = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrEmpty(fileName)) fileName = "download";
        var destination = GetUniqueFilePath(downloadsFolder, fileName);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file   = File.Create(destination);
        await stream.CopyToAsync(file, ct);

        return destination;
    }

    // ── HTML stripping ────────────────────────────────────────────────────────

    /// <summary>
    /// Strip HTML tags and decode entities, leaving readable plain text.
    /// Also removes scripts, styles, nav, header, footer, and collapses whitespace.
    /// Returns the cleaned text; <paramref name="imagesStripped"/> receives the number
    /// of image references that were removed (HTML &lt;img&gt; tags + Markdown ![]() syntax).
    /// </summary>
    public static string StripHtml(string html, out int imagesStripped)
    {
        if (string.IsNullOrWhiteSpace(html)) { imagesStripped = 0; return ""; }

        // Count image references before stripping
        int htmlImages = Regex.Matches(html, @"<img\b", RegexOptions.IgnoreCase).Count;
        int mdImages   = Regex.Matches(html, @"!\[.*?\]\(.*?\)").Count;
        imagesStripped = htmlImages + mdImages;

        // Remove script and style blocks entirely
        html = Regex.Replace(html, @"<(script|style|nav|header|footer|aside)[^>]*>.*?<\/\1>",
            "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Replace block-level tags with newlines
        html = Regex.Replace(html, @"<(br|p|div|h[1-6]|li|tr|blockquote)[^>]*>",
            "\n", RegexOptions.IgnoreCase);

        // Remove all remaining tags
        html = Regex.Replace(html, @"<[^>]+>", "");

        // Decode common HTML entities
        html = System.Net.WebUtility.HtmlDecode(html);

        // Collapse whitespace — keep single blank lines, remove runs of 3+
        html = Regex.Replace(html, @"[ \t]+", " ");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");

        return html.Trim();
    }

    /// <inheritdoc cref="StripHtml(string, out int)"/>
    public static string StripHtml(string html) => StripHtml(html, out _);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetUniqueFilePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            path = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(folder, $"{name}_{Guid.NewGuid():N}{ext}");
    }
}

// ── Supporting types ──────────────────────────────────────────────────────────

public sealed class WebFetchResult
{
    public bool   Success        { get; init; }
    public string Url            { get; init; } = "";
    public string Text           { get; init; } = "";
    public string ContentType    { get; init; } = "";
    public string ErrorReason    { get; init; } = "";
    public bool   WasBlocked     { get; init; }
    public bool   IsNonText      { get; init; }
    public int    ImagesStripped { get; init; }

    public static WebFetchResult Ok(string url, string text, string contentType, int imagesStripped = 0) =>
        new() { Success = true, Url = url, Text = text, ContentType = contentType, ImagesStripped = imagesStripped };

    public static WebFetchResult Fail(string url, string reason) =>
        new() { Success = false, Url = url, ErrorReason = reason };

    public static WebFetchResult Blocked(string url, string? reason = null) =>
        new() { Success = false, Url = url, WasBlocked = true,
                ErrorReason = reason ?? "Domain not in whitelist." };

    public static WebFetchResult NonText(string url, string contentType) =>
        new() { Success = false, Url = url, IsNonText = true, ContentType = contentType };

    /// <summary>Formats a compact injection string for the model context.</summary>
    public string ToInjectionString(string fetchedDate)
    {
        if (WasBlocked)  return $"[webfetch blocked — {ErrorReason}]";
        if (IsNonText)   return $"[webfetch skipped — non-text content ({ContentType}). Use a download tag if you need this file.]";
        if (!Success)    return $"[webfetch failed — {ErrorReason}]";

        var imageNote = ImagesStripped > 0
            ? $"\n[Note: {ImagesStripped} image{(ImagesStripped == 1 ? " was" : "s were")} stripped — this is text-only content. Any image-only sections will appear empty.]"
            : "";

        return $"[Web content from {new Uri(Url).Host} — {fetchedDate}]{imageNote}\n{Text}";
    }
}

/// <summary>A single entry in the web access whitelist.</summary>
public sealed class WebWhitelistEntry
{
    /// <summary>Domain or subdomain, e.g. "wikipedia.org" or "docs.python.org".</summary>
    public string Domain         { get; set; } = "";

    /// <summary>When false this entry is ignored without being deleted.</summary>
    public bool   IsEnabled      { get; set; } = true;

    /// <summary>Whether agents may download files from this domain.</summary>
    public bool   AllowDownloads { get; set; } = false;
}

/// <summary>Web browsing settings stored in AppSettings.</summary>
public sealed class WebBrowsingSettings
{
    /// <summary>Global whitelist. Project whitelists extend or override this.</summary>
    public List<WebWhitelistEntry> Whitelist { get; set; } = [];

    /// <summary>
    /// Master switch for file downloads.
    /// When false, agents may read web pages but DownloadFileAsync is never called.
    /// Default: false (downloads disabled until user opts in).
    /// </summary>
    public bool AllowDownloads { get; set; } = false;

    /// <summary>HTTP fetch timeout in seconds. Default 8.</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>Max characters returned per fetch for cloud/API models. Default 6 000.</summary>
    public int MaxCharsCloud { get; set; } = 6_000;

    /// <summary>Max characters returned per fetch for local models (Ollama, LM Studio, vLLM). Default 40 000.</summary>
    public int MaxCharsLocal { get; set; } = 40_000;

    /// <summary>File extensions downloaded without a user prompt. Default: .txt, .md, .pdf, .readme</summary>
    public List<string> AutoDownloadExtensions { get; set; } = [".txt", ".md", ".pdf", ".readme"];

    /// <summary>File extensions that require a user confirmation dialog before downloading.</summary>
    public List<string> AskDownloadExtensions  { get; set; } =
        [".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".odt", ".ods", ".odp", ".rtf"];
}
