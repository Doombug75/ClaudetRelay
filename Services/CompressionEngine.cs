using System.Text;

namespace ClaudetRelay.Services;

/// <summary>
/// Result of a successful compression run.
/// </summary>
public sealed record CompressionResult(
    IReadOnlyList<CloudAIMessage> NewHistory,
    int EstimatedTokens,
    int OriginalMessageCount,
    int SummaryPartCount,
    int TailMessageCount);

/// <summary>
/// Pure-logic context-compression engine.
/// No WPF or MainWindow dependencies — takes data in, returns data out.
/// </summary>
public sealed class CompressionEngine
{
    // ── Tuning constants ──────────────────────────────────────────────────────

    /// <summary>Fraction of the SMALLEST participant's context reserved as uncompressed live tail.</summary>
    private const double TailFraction = 0.15;

    /// <summary>Fraction of the MANAGER's context used per compression chunk (before overhead).</summary>
    private const double ChunkFraction = 0.25;

    /// <summary>Token reserve per chunk for the compression instruction + rolling context.</summary>
    private const int OverheadTokens = 350;

    /// <summary>Minimum viable chunk size (guards against tiny manager contexts).</summary>
    private const int MinChunkTokens = 400;

    /// <summary>Target summary size as a fraction of the chunk's token count.</summary>
    private const double SummaryRatio = 0.20;

    /// <summary>Minimum summary token budget (prevents tiny summaries vanishing).</summary>
    private const int SummaryMinTokens = 120;

    /// <summary>Messages shorter than this (in estimated tokens) are eligible for cluster-merging.</summary>
    private const int ShortMsgThreshold = 20;

    /// <summary>Minimum consecutive short messages required before merging them.</summary>
    private const int ShortClusterMinSize = 3;

    // ─────────────────────────────────────────────────────────────────────────

    private readonly TokenCalibration _cal;

    public CompressionEngine(TokenCalibration calibration) => _cal = calibration;

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Compresses the conversation history.
    /// </summary>
    /// <param name="fullHistory">Current _sharedHistory snapshot.</param>
    /// <param name="managerName">Display name of the participant doing the compression (for calibration).</param>
    /// <param name="managerContextWindow">Context window in tokens of the manager participant.</param>
    /// <param name="minParticipantContext">Smallest context window across all active participants.</param>
    /// <param name="callManager">
    ///   Delegate that sends (rollingContext, chunkContent, tokenBudget, ct) to the manager
    ///   and returns the summary text.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>CompressionResult, or null if there was nothing worth compressing.</returns>
    public async Task<CompressionResult?> CompressAsync(
        IReadOnlyList<CloudAIMessage> fullHistory,
        string  managerName,
        int     managerContextWindow,
        int     minParticipantContext,
        Func<string, string, int, CancellationToken, Task<string>> callManager,
        CancellationToken ct)
    {
        // Separate messages by kind
        var conversation   = fullHistory.Where(m => m.Role is "user" or "assistant").ToList();
        var priorSummaries = fullHistory.Where(m => m.Role == "system" && m.Sender == "compression").ToList();
        var otherSystemMsgs = fullHistory.Where(m => m.Role == "system" && m.Sender != "compression").ToList();

        if (conversation.Count < 4) return null;

        // 1. Split into tail (keep intact) and compress block
        int tailBudget = Math.Max((int)(minParticipantContext * TailFraction), 200);
        var (tail, compressBlock) = SplitTail(conversation, managerName, tailBudget);

        if (compressBlock.Count == 0) return null;

        // 2. Merge clusters of short messages to reduce noise
        var processed = MergeShortClusters(compressBlock, managerName);

        // 3. Flatten into token-budget-sized fragments, splitting at natural boundaries
        int chunkTokenBudget = Math.Max(
            (int)(managerContextWindow * ChunkFraction) - OverheadTokens,
            MinChunkTokens);

        var fragments = Flatten(processed, managerName, chunkTokenBudget);

        // 4. Group fragments into chunks
        var chunks = GroupIntoChunks(fragments, managerName, chunkTokenBudget);

        // 5. Rolling compression: seed with any prior summaries so they get re-compressed over time
        var rollingContext = priorSummaries.Count > 0
            ? string.Join("\n\n", priorSummaries.Select(m => m.Content))
            : "";
        var summaries = new List<string>();

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            string chunkText  = FormatChunk(chunk);
            int    chunkToks  = _cal.EstimateTokens(managerName, chunkText);
            int    budget     = Math.Max((int)(chunkToks * SummaryRatio), SummaryMinTokens);

            string summary = await callManager(rollingContext, chunkText, budget, ct);
            summary = summary.Trim();
            if (string.IsNullOrEmpty(summary)) summary = "(segment)";

            summaries.Add(summary);
            rollingContext = summary; // the latest summary IS the rolling context for the next chunk
        }

        // 6. Assemble new history
        string combinedSummary = summaries.Count == 1
            ? summaries[0]
            : string.Join("\n\n", summaries.Select((s, i) => $"[Part {i + 1}/{summaries.Count}]\n{s}"));

        var newHistory = new List<CloudAIMessage>();

        // Preserve non-compression system messages (config, injected prompts, etc.)
        // Prior compression summaries are NOT preserved — they've been absorbed into
        // the new rolling summary above and no longer need to live in history.
        newHistory.AddRange(otherSystemMsgs);

        // The new compression summary is stored with a sentinel sender so history builders
        // can append it to the system prompt instead of treating it as a chat turn.
        newHistory.Add(new CloudAIMessage(
            "system",
            $"[CONVERSATION HISTORY — COMPRESSED SUMMARY]\n{combinedSummary}\n[END OF SUMMARY]",
            "compression"));

        // Live tail — untouched
        newHistory.AddRange(tail);

        int estimatedNewTokens =
            _cal.EstimateTokens(managerName, combinedSummary) +
            tail.Sum(m => _cal.EstimateTokens(managerName, m.Content));

        return new CompressionResult(
            newHistory,
            estimatedNewTokens,
            fullHistory.Count,
            summaries.Count,
            tail.Count);
    }

    // ── Step 1: tail split ─────────────────────────────────────────────────

    private (List<CloudAIMessage> Tail, List<CloudAIMessage> CompressBlock)
        SplitTail(List<CloudAIMessage> messages, string managerName, int tailBudget)
    {
        int accumulated = 0;
        int cutIndex    = messages.Count; // exclusive upper bound of compress block

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            int est = _cal.EstimateTokens(managerName, messages[i].Content);
            if (accumulated + est > tailBudget)
            {
                cutIndex = i + 1;
                break;
            }
            accumulated += est;
            cutIndex = i;
        }

        var compressBlock = messages.Take(cutIndex).ToList();
        var tail          = messages.Skip(cutIndex).ToList();
        return (tail, compressBlock);
    }

    // ── Step 2: merge short clusters ──────────────────────────────────────

    private List<CloudAIMessage> MergeShortClusters(
        List<CloudAIMessage> messages, string managerName)
    {
        var result = new List<CloudAIMessage>(messages.Count);
        var cluster = new List<CloudAIMessage>();

        void FlushCluster()
        {
            if (cluster.Count == 0) return;
            if (cluster.Count >= ShortClusterMinSize)
            {
                // Summarise the cluster as a single annotation
                var preview = string.Join(" / ", cluster.Take(3).Select(m =>
                    Truncate(m.Content, 40)));
                result.Add(new CloudAIMessage("user",
                    $"[{cluster.Count} brief exchanges: {preview}{(cluster.Count > 3 ? " …" : "")}]",
                    "System"));
            }
            else
            {
                result.AddRange(cluster);
            }
            cluster.Clear();
        }

        foreach (var msg in messages)
        {
            if (_cal.EstimateTokens(managerName, msg.Content) < ShortMsgThreshold)
                cluster.Add(msg);
            else
            {
                FlushCluster();
                result.Add(msg);
            }
        }
        FlushCluster();
        return result;
    }

    // ── Step 3: flatten into fragments ────────────────────────────────────

    private readonly record struct MessageFragment(
        CloudAIMessage Source,
        string         Text,
        bool           IsContinuation);

    private List<MessageFragment> Flatten(
        List<CloudAIMessage> messages, string managerName, int maxTokensPerFragment)
    {
        var fragments = new List<MessageFragment>();

        foreach (var msg in messages)
        {
            var parts = SplitAtNaturalBoundary(msg.Content, managerName, maxTokensPerFragment);
            for (int i = 0; i < parts.Count; i++)
                fragments.Add(new MessageFragment(msg, parts[i], IsContinuation: i > 0));
        }

        return fragments;
    }

    // ── Step 4: group fragments into chunks ───────────────────────────────

    private List<List<MessageFragment>> GroupIntoChunks(
        List<MessageFragment> fragments, string managerName, int tokenBudget)
    {
        var chunks  = new List<List<MessageFragment>>();
        var current = new List<MessageFragment>();
        int currentTokens = 0;

        foreach (var frag in fragments)
        {
            int ft = _cal.EstimateTokens(managerName, frag.Text);
            if (current.Count > 0 && currentTokens + ft > tokenBudget)
            {
                chunks.Add(current);
                current = [];
                currentTokens = 0;
            }
            current.Add(frag);
            currentTokens += ft;
        }

        if (current.Count > 0) chunks.Add(current);
        return chunks;
    }

    // ── Step 5 helpers: format chunk for the compression prompt ───────────

    private static string FormatChunk(List<MessageFragment> fragments)
    {
        var sb = new StringBuilder();
        foreach (var frag in fragments)
        {
            var sender = string.IsNullOrEmpty(frag.Source.Sender)
                ? (frag.Source.Role == "user" ? "User" : "AI")
                : frag.Source.Sender;

            sb.Append('[').Append(sender);
            if (frag.IsContinuation) sb.Append(", continued");
            sb.AppendLine("]");
            sb.AppendLine(frag.Text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ── Natural boundary splitter ─────────────────────────────────────────

    /// <summary>
    /// Splits text into parts, each ≤ maxTokens, preferring natural break points:
    /// paragraph break → sentence end → clause end → word boundary.
    /// Never splits mid-word.
    /// </summary>
    public IReadOnlyList<string> SplitAtNaturalBoundary(
        string text, string participantName, int maxTokens)
    {
        if (_cal.EstimateTokens(participantName, text) <= maxTokens)
            return [text];

        var parts = new List<string>();
        var remaining = text.AsSpan();

        while (true)
        {
            if (_cal.EstimateTokens(participantName, remaining.ToString()) <= maxTokens)
            {
                if (!remaining.IsEmpty) parts.Add(remaining.ToString());
                break;
            }

            int targetChars = (int)(maxTokens * _cal.GetCharsPerToken(participantName));
            targetChars = Math.Clamp(targetChars, 1, remaining.Length - 1);

            int splitAt = FindSplitPoint(remaining, targetChars);

            parts.Add(remaining[..splitAt].TrimEnd().ToString());
            remaining = remaining[splitAt..].TrimStart();
        }

        return parts;
    }

    private static int FindSplitPoint(ReadOnlySpan<char> text, int targetChars)
    {
        int search = Math.Min(targetChars, text.Length - 1);
        int half   = search / 2;

        // 1. Paragraph break (\n\n)
        for (int i = search; i >= half; i--)
        {
            if (i + 1 < text.Length && text[i] == '\n' && text[i + 1] == '\n')
                return i + 2;
        }

        // 2. Sentence end (". " or ".\n" or "! " or "? ")
        for (int i = search; i >= half; i--)
        {
            if (i + 1 < text.Length &&
                text[i + 1] is ' ' or '\n' &&
                text[i] is '.' or '!' or '?')
                return i + 1;
        }

        // 3. Clause boundary (", " or "; ")
        for (int i = search; i >= half; i--)
        {
            if (i + 1 < text.Length && text[i + 1] == ' ' && text[i] is ',' or ';')
                return i + 1;
        }

        // 4. Word boundary
        for (int i = search; i >= half; i--)
        {
            if (text[i] == ' ')
                return i + 1;
        }

        // 5. Hard cut (degenerate — very long token with no spaces)
        return search;
    }

    // ── Utility ───────────────────────────────────────────────────────────

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
