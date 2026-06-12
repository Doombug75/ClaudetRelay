using System.Text;
using System.Windows;
using ClaudetRelay.Properties;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Context-compression wiring for MainWindow.
/// Holds the calibration singleton, the re-entrancy guard, and the trigger/manager logic.
/// </summary>
public partial class MainWindow
{
    // ── Shared calibration & engine ───────────────────────────────────────────
    internal readonly TokenCalibration _tokenCalibration = new();

    private CompressionEngine? _engineBacking;
    private CompressionEngine  Engine => _engineBacking ??= new CompressionEngine(_tokenCalibration);

    // Prevents concurrent compression runs (one at a time, no queue)
    private volatile bool _compressionInProgress;

    // Set while a multi-page file read is in progress; compression waits until it clears.
    internal volatile bool _fileReadInProgress;

    // ── 80 % trigger (called after every participant response) ────────────────

    /// <summary>
    /// Called after any participant finishes a response.
    /// Checks whether any participant has hit ≥80 % of its context window.
    /// If so, and the manager is available, runs the compression pipeline.
    /// Fire-and-forget safe: errors are surfaced as system messages, never thrown.
    /// </summary>
    internal void TriggerCompressionCheck()
    {
        if (_compressionInProgress) return;
        if (_fileReadInProgress) return;   // wait until multi-page read completes
        if (!AnyParticipantAtCapacity()) return;
        _ = RunCompressionAsync();
    }

    private bool AnyParticipantAtCapacity()
    {
        foreach (var ui in _ollamaParticipants)
        {
            if (!ui.Data.Enabled) continue;
            int ctx = ui.Data.Service.NumCtx;
            int last = ui.Data.Service.LastUsage?.InputTokens ?? 0;
            if (ctx > 0 && last >= ctx * 0.65) return true;
        }
        foreach (var ui in _cloudAIParticipants)
        {
            if (!ui.Data.Enabled) continue;
            int ctx = ui.Data.Service.ContextWindowTokens;
            int last = ui.Data.Service.LastUsage?.InputTokens ?? 0;
            if (ctx > 0 && last >= ctx * 0.65) return true;
        }
        return false;
    }

    // ── Compression UI helpers ────────────────────────────────────────────────

    private void BeginCompressionUI()
    {
        CompressionProgressArea.Visibility = Visibility.Visible;
        SendButton.IsEnabled               = false;
        AIRespondButton.IsEnabled          = false;
    }

    private void EndCompressionUI(string statusLabel)
    {
        CompressionProgressArea.Visibility = Visibility.Collapsed;
        CompressionProgressLabel.Text      = Loc.S("Compression_ProgressLabel"); // reset for next run
        SendButton.IsEnabled               = true;
        AIRespondButton.IsEnabled          = true;
        AddSystemMessage(statusLabel);
    }

    // ── Core compression task ─────────────────────────────────────────────────

    private async Task RunCompressionAsync()
    {
        if (_compressionInProgress) return;
        _compressionInProgress = true;

        Dispatcher.Invoke(() =>
        {
            BeginCompressionUI();
            AddSystemMessage(Loc.S("Compression_Starting"));
        });

        try
        {
            var (managerName, managerCtx, callManager) = FindCompressionManager();
            if (callManager is null)
            {
                Dispatcher.Invoke(() => EndCompressionUI(Loc.S("Compression_NoManager")));
                return;
            }

            int minCtx = MinActiveContextWindow();

            var result = await Engine.CompressAsync(
                _sharedHistory.ToList(),   // snapshot — engine is stateless
                managerName,
                managerCtx,
                minCtx,
                callManager,
                CancellationToken.None);

            if (result is null)
            {
                Dispatcher.Invoke(() => EndCompressionUI(Loc.S("Compression_TooShort")));
                return;
            }

            // Replace shared history with the compressed version
            _sharedHistory.Clear();
            _sharedHistory.AddRange(result.NewHistory);

            // Reset all participant session-token counters to the estimated new history size.
            // This prevents them from staying in the red immediately after compression.
            int newTokens = result.EstimatedTokens;
            foreach (var ui in _ollamaParticipants)
            {
                ui.SessionInputTokens  = newTokens;
                ui.SessionOutputTokens = 0;
            }
            foreach (var ui in _cloudAIParticipants)
            {
                ui.SessionInputTokens  = newTokens;
                ui.SessionOutputTokens = 0;
            }

            Dispatcher.Invoke(() =>
            {
                // Refresh all context bars to reflect the new token counts
                foreach (var ui in _ollamaParticipants)
                {
                    int ctx = ui.Data.Service.NumCtx;
                    UpdateContextBar(ui.ContextBar, newTokens, ctx);
                    UpdatePopupStats(ui.PopupContextVal, ui.PopupSessionVal,
                        newTokens, ctx, newTokens, 0);
                }
                foreach (var ui in _cloudAIParticipants)
                {
                    int ctx = ui.Data.Service.ContextWindowTokens;
                    UpdateContextBar(ui.ContextBar, newTokens, ctx);
                    UpdatePopupStats(ui.PopupContextVal, ui.PopupSessionVal,
                        newTokens, ctx, newTokens, 0);
                }

                int removed = result.OriginalMessageCount - result.TailMessageCount;
                EndCompressionUI(string.Format(
                    Loc.S("Compression_Done"),
                    removed, result.SummaryPartCount, result.TailMessageCount));
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() => EndCompressionUI(Loc.S("Compression_Cancelled")));
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => EndCompressionUI(string.Format(Loc.S("Compression_Failed"), ex.Message)));
        }
        finally
        {
            _compressionInProgress = false;
        }
    }

    // ── Manager resolution ────────────────────────────────────────────────────

    private (string Name, int ContextWindow,
             Func<string, string, int, CancellationToken, Task<string>>? Call)
        FindCompressionManager()
    {
        var savedName = SettingsService.Load().CompressionParticipantName;

        // 1. Try named participant (settings)
        if (!string.IsNullOrEmpty(savedName))
        {
            foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
            {
                if (ui.Data.DisplayName.Equals(savedName, StringComparison.OrdinalIgnoreCase))
                    return (ui.Data.DisplayName, ui.Data.Service.NumCtx, MakeOllamaCall(ui));
            }
            foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
            {
                if (ui.Data.DisplayName.Equals(savedName, StringComparison.OrdinalIgnoreCase))
                    return (ui.Data.DisplayName, ui.Data.Service.ContextWindowTokens, MakeCloudCall(ui));
            }
        }

        // 2. Auto: pick the participant with the largest context window
        OllamaParticipantUI?  bestOllama = null;
        CloudAIParticipantUI? bestCloud  = null;
        int bestCtx = 0;

        foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
        {
            int ctx = ui.Data.Service.NumCtx;
            if (ctx > bestCtx) { bestCtx = ctx; bestOllama = ui; bestCloud = null; }
        }
        foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
        {
            int ctx = ui.Data.Service.ContextWindowTokens;
            if (ctx > bestCtx) { bestCtx = ctx; bestCloud = ui; bestOllama = null; }
        }

        if (bestOllama is not null)
            return (bestOllama.Data.DisplayName, bestOllama.Data.Service.NumCtx, MakeOllamaCall(bestOllama));
        if (bestCloud  is not null)
            return (bestCloud.Data.DisplayName,  bestCloud.Data.Service.ContextWindowTokens, MakeCloudCall(bestCloud));

        return ("", 0, null);
    }

    // ── Manager call delegates ────────────────────────────────────────────────

    private Func<string, string, int, CancellationToken, Task<string>> MakeOllamaCall(
        OllamaParticipantUI ui)
    {
        return async (rollingContext, chunkContent, budget, ct) =>
        {
            var svc    = ui.Data.Service;
            var name   = ui.Data.DisplayName;
            var system = CompressionSystemPrompt();
            var user   = CompressionUserPrompt(rollingContext, chunkContent, budget);

            var msgs = new List<OllamaChatMessage>
            {
                new("system", system),
                new("user",   user)
            };

            var sb = new StringBuilder();
            await foreach (var tok in svc.StreamAsync(msgs, ct))
                sb.Append(tok);

            // Calibrate from this call
            if (svc.LastUsage is { } u && u.InputTokens > 0)
                _tokenCalibration.Record(name, system.Length + user.Length, u.InputTokens);

            return sb.ToString().Trim();
        };
    }

    private Func<string, string, int, CancellationToken, Task<string>> MakeCloudCall(
        CloudAIParticipantUI ui)
    {
        return async (rollingContext, chunkContent, budget, ct) =>
        {
            var svc    = ui.Data.Service;
            var name   = ui.Data.DisplayName;
            var system = CompressionSystemPrompt();
            var user   = CompressionUserPrompt(rollingContext, chunkContent, budget);

            var msgs = new List<CloudAIMessage>
            {
                new("user", user)
            };

            var sb = new StringBuilder();
            await foreach (var tok in svc.StreamAsync(msgs, system, ct))
                sb.Append(tok);

            if (svc.LastUsage is { } u && u.InputTokens > 0)
                _tokenCalibration.Record(name, system.Length + user.Length, u.InputTokens);

            return sb.ToString().Trim();
        };
    }

    // ── Compression prompts ───────────────────────────────────────────────────

    private static string CompressionSystemPrompt() =>
        "You are a conversation summariser. " +
        "Your sole task is to produce a compact but complete record of the conversation segment provided.\n\n" +
        "Rules:\n" +
        "• Preserve ALL decisions made, facts established, names and identifiers mentioned, " +
        "files referenced, and any open questions or next steps.\n" +
        "• Code blocks: summarise as signatures with a one-line description " +
        "(e.g. `DrawExplosion(name: string, x: int, y: int)` — renders explosion sprite at coords). " +
        "Never paste large code verbatim; preserve the interface and purpose, not the implementation.\n" +
        "• Do NOT add analysis, commentary, or opinions not present in the original.\n" +
        "• Write in neutral third-person: \"User asked about X. [Name] explained that Y. They decided Z.\"\n" +
        "• If a rolling context is provided, your summary CONTINUES from it — do not repeat what is already in the context.\n" +
        "• Tighter is better as long as no key information is lost.";

    private static string CompressionUserPrompt(
        string rollingContext, string chunkContent, int budget)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(rollingContext))
        {
            sb.AppendLine("PREVIOUS CONTEXT (already summarised — treat as established, do not repeat):");
            sb.AppendLine(rollingContext);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine("CONVERSATION SEGMENT TO SUMMARISE:");
        sb.AppendLine(chunkContent);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append($"Write a concise summary that continues naturally from the previous context (if any). " +
                  $"Token budget: ≤{budget} tokens.");

        return sb.ToString();
    }

    // ── Context window helpers ────────────────────────────────────────────────

    private int MinActiveContextWindow()
    {
        int min = int.MaxValue;
        foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
        {
            int ctx = ui.Data.Service.NumCtx;
            if (ctx > 0) min = Math.Min(min, ctx);
        }
        foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
        {
            int ctx = ui.Data.Service.ContextWindowTokens;
            if (ctx > 0) min = Math.Min(min, ctx);
        }
        return min == int.MaxValue ? 8192 : min;
    }
}
