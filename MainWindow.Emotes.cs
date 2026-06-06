using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudetRelay;

/// <summary>
/// IRC-style emote rendering for ClaudetRelay chat bubbles.
///
/// Syntax understood by both users and AI models:
///   /me &lt;action&gt;              — entire message is an emote; user/AI name is prepended
///                                  e.g.  /me waves  →  "Robert waves"  (highlighted)
///   *&lt;text&gt;*                   — inline emote within normal text
///                                  e.g.  Hello *grins* how are you?
///   /me &lt;action&gt;* &lt;speech&gt;  — emote start then normal text (the * closes the emote)
///                                  e.g.  /me waves* Hey there *smiles*
///
/// Raw text (with * markers and /me prefix) is stored in _sharedHistory and log files
/// unchanged.  Formatting is a pure display concern applied when building or finalising
/// a chat bubble.
/// </summary>
public partial class MainWindow
{
    // ── Parse ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits <paramref name="raw"/> into alternating normal/emote segments.
    /// When the message starts with "/me ", the leading text is emote-mode and
    /// <paramref name="senderName"/> (if provided) is prepended so the result
    /// reads as "Robert waves" rather than just "waves".
    /// </summary>
    private static IEnumerable<(string Text, bool IsEmote)> ParseEmoteSegments(
        string raw, string senderName = "")
    {
        if (string.IsNullOrEmpty(raw)) yield break;

        bool startsEmote = false;
        string text = raw;

        if (raw.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
        {
            var action = raw[4..].TrimStart();
            text = string.IsNullOrEmpty(senderName) ? action : $"{senderName} {action}";
            startsEmote = true;
        }
        else if (raw.Equals("/me", StringComparison.OrdinalIgnoreCase))
        {
            text = senderName;
            startsEmote = true;
        }

        // Split on single asterisks; adjacent ** produce an empty segment (swallowed).
        var parts = text.Split('*');
        bool emote = startsEmote;

        foreach (var part in parts)
        {
            if (part.Length > 0)
                yield return (part, emote);
            emote = !emote;
        }
    }

    // ── Render ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the visible content of a chat bubble with emote formatting applied.
    ///
    /// <para>The <see cref="StreamBubble"/> holds two content layers:</para>
    /// <list type="bullet">
    ///   <item><see cref="StreamBubble.Content"/> — a <see cref="System.Windows.Controls.TextBox"/>
    ///         that always holds the raw text (for copy/select).</item>
    ///   <item><see cref="StreamBubble.EmoteContent"/> — a <see cref="TextBlock"/> with
    ///         mixed <see cref="Run"/>/<see cref="Span"/> inlines, shown instead of
    ///         Content when emote markers are present.</item>
    /// </list>
    ///
    /// Fast path: no <c>*</c> or <c>/me</c> → only <c>Content.Text</c> is updated,
    /// <c>EmoteContent</c> stays collapsed.
    ///
    /// Rich path: <c>EmoteContent.Inlines</c> is rebuilt with italic
    /// <see cref="Span"/> elements bound to <c>AccentHighlightBrush</c>, then
    /// <c>EmoteContent</c> is shown and <c>Content</c> is hidden (but still holds the
    /// raw text so the copy button works).
    /// </summary>
    private void ApplyEmoteFormatting(StreamBubble bubble, string text, string senderName = "")
    {
        // Always keep the raw text in Content so the copy button can access it.
        bubble.Content.Text = text;

        bool hasEmote = !string.IsNullOrEmpty(text)
            && (text.Contains('*')
                || text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase)
                || text.Equals("/me", StringComparison.OrdinalIgnoreCase));

        if (!hasEmote)
        {
            // Fast path — show plain TextBox, hide the emote TextBlock.
            bubble.Content.Visibility      = Visibility.Visible;
            bubble.EmoteContent.Visibility = Visibility.Collapsed;
            bubble.EmoteContent.Inlines.Clear();
            return;
        }

        // Rich path — build inline runs in EmoteContent.
        bubble.EmoteContent.Inlines.Clear();

        foreach (var (segment, isEmote) in ParseEmoteSegments(text, senderName))
        {
            if (isEmote)
            {
                // Span supports SetResourceReference (FrameworkContentElement),
                // so the colour tracks theme changes automatically.
                var span = new Span(new Run(segment))
                {
                    FontStyle = FontStyles.Italic
                };
                span.SetResourceReference(
                    TextElement.ForegroundProperty, "AccentHighlightBrush");
                bubble.EmoteContent.Inlines.Add(span);
            }
            else
            {
                // Normal text inherits EmoteContent's foreground (bubbleTextKey).
                bubble.EmoteContent.Inlines.Add(new Run(segment));
            }
        }

        // Swap visibility: show formatted TextBlock, hide raw TextBox.
        bubble.Content.Visibility      = Visibility.Collapsed;
        bubble.EmoteContent.Visibility = Visibility.Visible;
    }

    // ── System-prompt snippet ──────────────────────────────────────────────

    /// <summary>
    /// Returns a brief instruction block telling the model about emote syntax.
    /// Appended to every system prompt so models can use and understand emotes.
    /// </summary>
    private static string BuildEmoteInstruction() =>
        "\n\n## Emotes & actions" +
        "\nThis chat supports IRC-style emotes. You can use them naturally when they fit:" +
        "\n• Wrap action text in asterisks to express an inline emote:" +
        "\n  e.g.  That's a great point! *smiles*  or  *thinks for a moment*  Then yes, I agree." +
        "\n• Start a message with /me to make the entire opening phrase an action:" +
        "\n  e.g.  /me considers the problem carefully* Okay, here is my thinking…" +
        "\n  (The * closes the action and the rest is treated as normal speech.)" +
        "\nEmote text appears highlighted and italic in the UI. Use emotes sparingly and " +
        "only when they genuinely add warmth or clarity — do not force them.";

    /// <summary>
    /// Returns a brief instruction block about file access modes.
    /// Appended to every system prompt so models understand read-only vs read-write requests.
    /// </summary>
    private static string BuildFileAccessInstruction() =>
        "\n\n## File access & locking" +
        "\nWhen you need to access files, request the appropriate access mode:" +
        "\n• Read-only access: Use when you only need to read/understand file contents." +
        "\n  This is fast and doesn't block other participants from reading the same file." +
        "\n• Read-write access: Use ONLY when you need to modify or write to a file." +
        "\n  This locks the file so others must wait until you're done." +
        "\nBe considerate: request read-only when reading, read-write only when modifying. " +
        "This allows parallel work when multiple participants are researching the same topic." +
        "\n\n### Readable file formats" +
        "\nYou can read the following types from the INPUT/ folder:" +
        "\n• Plain text: .txt, .md, .rst, .html, .csv" +
        "\n• Word documents: .docx (text and tables extracted automatically)" +
        "\n• Excel spreadsheets: .xlsx (each sheet rendered as a Markdown table)" +
        "\n• PowerPoint presentations: .pptx (slide text, one section per slide)" +
        "\n• LibreOffice Writer: .odt  •  Calc: .ods  •  Impress: .odp" +
        "\nLegacy Office formats (.doc, .xls, .ppt) are NOT supported — ask the user to" +
        " re-save as a modern format first.";
}
