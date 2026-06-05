using System.Net;
using System.Text;
using ClaudetRelay.Services;

namespace ClaudetRelay.Services;

/// <summary>Generates self-contained export documents from a project's chat log.</summary>
public static class ExportService
{
    // ── Colour palette for AI bubbles in HTML ──────────────────────────────
    // Each distinct AI participant (by AvatarLabel) gets a stable colour slot.
    private static readonly (string Bg, string Border, string Name)[] AiColours =
    [
        ("#e3f2fd", "#90caf9", "#1565c0"),   // blue
        ("#e8f5e9", "#a5d6a7", "#2e7d32"),   // green
        ("#fff3e0", "#ffcc80", "#e65100"),   // orange
        ("#f3e5f5", "#ce93d8", "#6a1b9a"),   // purple
        ("#fce4ec", "#f48fb1", "#880e4f"),   // pink
        ("#e0f7fa", "#80deea", "#006064"),   // teal
        ("#fffde7", "#fff176", "#f57f17"),   // yellow
        ("#fbe9e7", "#ffab91", "#bf360c"),   // deep orange
    ];

    // ── HTML export ────────────────────────────────────────────────────────

    public static string GenerateHtml(string projectName, List<ChatLogEntry> entries,
                                       string fontFamily = "Segoe UI", double fontSize = 13.0,
                                       double bubbleWidthPercent = 78.0)
    {
        // Assign colours to AI participants in order of first appearance
        var colourMap  = new Dictionary<string, int>(StringComparer.Ordinal);
        var colourNext = 0;

        int GetColourIndex(string avatarLabel)
        {
            if (colourMap.TryGetValue(avatarLabel, out var idx)) return idx;
            idx = colourNext++ % AiColours.Length;
            colourMap[avatarLabel] = idx;
            return idx;
        }

        var bubbles = new StringBuilder();

        foreach (var entry in entries)
        {
            var time    = entry.Timestamp.ToString("yyyy-MM-dd  HH:mm");
            var content = WebUtility.HtmlEncode(entry.Message);

            switch (entry.SenderType)
            {
                case "User":
                    bubbles.Append(
                        $"""
                        <div class="msg user">
                          <div class="meta">You · {time}</div>
                          <div class="bubble">{content}</div>
                        </div>
                        """);
                    break;

                case "AI":
                {
                    var idx   = GetColourIndex(entry.AvatarLabel);
                    var (bg, border, nameCol) = AiColours[idx];
                    var name  = WebUtility.HtmlEncode(entry.DisplayName);
                    bubbles.Append(
                        $"""
                        <div class="msg ai" style="--ai-bg:{bg};--ai-border:{border};--ai-name:{nameCol}">
                          <div class="meta" style="color:{nameCol}">{name} · {time}</div>
                          <div class="bubble">{content}</div>
                        </div>
                        """);
                    break;
                }

                case "System":
                    bubbles.Append(
                        $"""
                        <div class="msg system">
                          <div class="bubble">{content}</div>
                        </div>
                        """);
                    break;
            }
        }

        var exportDate    = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var msgCount      = entries.Count(e => e.SenderType != "System");
        var safeTitle     = WebUtility.HtmlEncode(projectName);
        var cssFontFamily    = $"'{fontFamily}', system-ui, -apple-system, sans-serif";
        var cssFontSize      = $"{fontSize:0.##}px";
        var cssBubbleMaxWidth = $"{bubbleWidthPercent:0.#}%";

        return
            $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>{{safeTitle}} — ClaudetRelay Export</title>
              <style>
                *, *::before, *::after { box-sizing: border-box; }
                body {
                  font-family: {{cssFontFamily}};
                  font-size: {{cssFontSize}};
                  background: #f2f3f5;
                  margin: 0; padding: 24px 16px;
                  color: #1a1a1a;
                }
                .page { max-width: 820px; margin: 0 auto; }
                .header {
                  background: white; border-radius: 12px;
                  padding: 20px 24px; margin-bottom: 20px;
                  box-shadow: 0 1px 4px rgba(0,0,0,.08);
                }
                .header h1 { margin: 0 0 6px; font-size: 20px; font-weight: 700; }
                .header p  { margin: 0; color: #666; font-size: 13px; }
                .chat { display: flex; flex-direction: column; gap: 10px; }
                .msg { display: flex; flex-direction: column; }
                .meta {
                  font-size: 11px; color: #888;
                  margin-bottom: 4px; padding: 0 4px;
                }
                .bubble {
                  display: inline-block; max-width: {{cssBubbleMaxWidth}};
                  padding: 10px 14px; border-radius: 14px;
                  line-height: 1.55;
                  white-space: pre-wrap; word-break: break-word;
                }
                /* User */
                .user { align-items: flex-end; }
                .user .meta { text-align: right; }
                .user .bubble {
                  background: #0078d4; color: white;
                  border-radius: 14px 14px 2px 14px;
                }
                /* AI */
                .ai  { align-items: flex-start; }
                .ai .bubble {
                  background: var(--ai-bg, #f0f0f0);
                  border: 1px solid var(--ai-border, #ddd);
                  border-radius: 2px 14px 14px 14px;
                }
                /* System */
                .system { align-items: center; margin: 4px 0; }
                .system .bubble {
                  background: transparent; color: #aaa;
                  font-size: 12px; font-style: italic;
                  padding: 2px 12px; border: none;
                }
                .footer {
                  margin-top: 28px; text-align: center;
                  font-size: 12px; color: #bbb;
                }
              </style>
            </head>
            <body>
              <div class="page">
                <div class="header">
                  <h1>{{safeTitle}}</h1>
                  <p>Exported from ClaudetRelay · {{exportDate}} · {{msgCount}} message{{(msgCount == 1 ? "" : "s")}}</p>
                </div>
                <div class="chat">
            {{bubbles}}
                </div>
                <div class="footer">Generated by ClaudetRelay</div>
              </div>
            </body>
            </html>
            """;
    }

    // ── Markdown export ────────────────────────────────────────────────────

    public static string GenerateMarkdown(string projectName, List<ChatLogEntry> entries)
    {
        var sb         = new StringBuilder();
        var exportDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var msgCount   = entries.Count(e => e.SenderType != "System");

        sb.AppendLine($"# {projectName}");
        sb.AppendLine();
        sb.AppendLine($"*Exported from ClaudetRelay · {exportDate} · {msgCount} message{(msgCount == 1 ? "" : "s")}*");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            var time = entry.Timestamp.ToString("yyyy-MM-dd HH:mm");

            switch (entry.SenderType)
            {
                case "User":
                    sb.AppendLine($"### 👤 You · *{time}*");
                    sb.AppendLine();
                    sb.AppendLine(entry.Message);
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                    break;

                case "AI":
                    sb.AppendLine($"### 🤖 {entry.DisplayName} · *{time}*");
                    sb.AppendLine();
                    sb.AppendLine(entry.Message);
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                    break;

                case "System":
                    sb.AppendLine($"> *{entry.Message}*");
                    sb.AppendLine();
                    break;
            }
        }

        sb.AppendLine("---");
        sb.AppendLine("*Generated by ClaudetRelay*");

        return sb.ToString();
    }

}
