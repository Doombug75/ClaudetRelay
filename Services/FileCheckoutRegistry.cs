using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudetRelay.Services;

/// <summary>
/// Tracks which participants have files checked out for reading/writing.
/// Prevents conflicts when multiple participants work on the same files in parallel.
///
/// Read-only checkouts: only block other writes (multiple reads OK)
/// Read-write checkouts: block both reads and writes (exclusive access)
///
/// Timeouts: stale checkouts (5+ min) are flagged if participant isn't actively busy.
/// </summary>
public class FileCheckoutRegistry
{
    /// <summary>Checkout mode: read-only or read-write (exclusive).</summary>
    public enum CheckoutMode { ReadOnly, ReadWrite }

    /// <summary>Info about a file checkout: who has it, when, and why.</summary>
    public record CheckoutInfo(
        string ParticipantName,
        DateTime CheckoutTime,
        CheckoutMode Mode);

    /// <summary>Info about file modifications: which participant wrote what and when.</summary>
    public record ModificationInfo(
        string FilePath,
        string ParticipantName,
        string FileHash,
        DateTime ModificationTime);

    private readonly Dictionary<string, CheckoutInfo> _checkouts = new();
    private readonly Dictionary<string, string> _originalHashes = new();  // Original before any edits
    private readonly List<ModificationInfo> _modifications = new();

    // ── Checkout operations ──────────────────────────────────────────────────

    /// <summary>
    /// Attempt to check out a file for a participant.
    /// Returns true if successful, false if already checked out by someone else.
    /// </summary>
    public bool TryCheckout(
        string filePath,
        string participantName,
        CheckoutMode mode,
        out string? reason)
    {
        reason = null;
        var normalized = NormalizePath(filePath);

        if (_checkouts.TryGetValue(normalized, out var existing))
        {
            // Someone else has it checked out in write mode? Always block.
            if (existing.Mode == CheckoutMode.ReadWrite)
            {
                reason = $"checked out by {existing.ParticipantName} (exclusive edit)";
                return false;
            }

            // Someone else has it in read-only mode. Block only if we want write.
            if (mode == CheckoutMode.ReadWrite)
            {
                reason = $"in use by {existing.ParticipantName} (reading)";
                return false;
            }

            // Both read-only: allow concurrent reads
            return true;
        }

        // Not checked out — proceed
        _checkouts[normalized] = new CheckoutInfo(participantName, DateTime.UtcNow, mode);

        // Capture original file hash (for conflict detection later)
        if (!_originalHashes.ContainsKey(normalized))
            _originalHashes[normalized] = ComputeFileHash(filePath);

        return true;
    }

    /// <summary>Check in a file, releasing the checkout.</summary>
    public void Checkin(string filePath)
    {
        var normalized = NormalizePath(filePath);
        _checkouts.Remove(normalized);
    }

    /// <summary>Check in all files for a specific participant (cleanup after task completes).</summary>
    public void CheckinByParticipant(string participantName)
    {
        var files = _checkouts
            .Where(kvp => kvp.Value.ParticipantName == participantName)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var file in files)
            _checkouts.Remove(file);
    }

    /// <summary>Check in ALL files (call on project load to reset state).</summary>
    public void CheckinAll()
    {
        _checkouts.Clear();
        _originalHashes.Clear();
        _modifications.Clear();
    }

    /// <summary>
    /// Refresh (reset) the checkout time for a file, extending the stale-checkout window.
    /// Call this when the participant is busy and can't respond to a checkin reminder yet.
    /// </summary>
    public void RefreshCheckout(string filePath)
    {
        var normalized = NormalizePath(filePath);
        if (_checkouts.TryGetValue(normalized, out var existing))
            _checkouts[normalized] = existing with { CheckoutTime = DateTime.UtcNow };
    }

    // ── Status queries ──────────────────────────────────────────────────────

    /// <summary>True if any file is currently checked out by any participant.</summary>
    public bool HasAnyCheckout => _checkouts.Count > 0;

    /// <summary>Returns all current checkouts (file path → checkout info).</summary>
    public IEnumerable<(string FilePath, CheckoutInfo Info)> GetAllCheckouts()
        => _checkouts.Select(kvp => (kvp.Key, kvp.Value));

    /// <summary>Get checkout info for a file, or null if not checked out.</summary>
    public CheckoutInfo? GetCheckout(string filePath)
    {
        var normalized = NormalizePath(filePath);
        return _checkouts.TryGetValue(normalized, out var info) ? info : null;
    }

    /// <summary>Check if a file is currently checked out by anyone.</summary>
    public bool IsCheckedOut(string filePath)
        => _checkouts.ContainsKey(NormalizePath(filePath));

    /// <summary>Get the participant who has this file checked out (if any).</summary>
    public string? GetCheckedOutBy(string filePath)
    {
        var normalized = NormalizePath(filePath);
        return _checkouts.TryGetValue(normalized, out var info) ? info.ParticipantName : null;
    }

    /// <summary>Get how long a file has been checked out.</summary>
    public TimeSpan GetCheckoutDuration(string filePath)
    {
        var normalized = NormalizePath(filePath);
        if (_checkouts.TryGetValue(normalized, out var info))
            return DateTime.UtcNow - info.CheckoutTime;
        return TimeSpan.Zero;
    }

    /// <summary>Check if a checkout is stale (older than maxDuration).</summary>
    public bool IsCheckoutStale(string filePath, TimeSpan maxDuration)
    {
        var normalized = NormalizePath(filePath);
        if (_checkouts.TryGetValue(normalized, out var info))
            return DateTime.UtcNow - info.CheckoutTime > maxDuration;
        return false;
    }

    /// <summary>Get all stale checkouts (not updated recently).</summary>
    public IEnumerable<(string filePath, CheckoutInfo checkout)> GetStaleCheckouts(TimeSpan maxDuration)
    {
        return _checkouts
            .Where(kvp => DateTime.UtcNow - kvp.Value.CheckoutTime > maxDuration)
            .Select(kvp => (kvp.Key, kvp.Value));
    }

    // ── Modification tracking (for conflict detection) ─────────────────────

    /// <summary>Record that a participant modified a file.</summary>
    public void RecordModification(string filePath, string participantName)
    {
        var normalized = NormalizePath(filePath);
        var hash = ComputeFileHash(filePath);

        _modifications.Add(new ModificationInfo(
            normalized,
            participantName,
            hash,
            DateTime.UtcNow));
    }

    /// <summary>Get the original hash of a file (before any modifications).</summary>
    public string? GetOriginalHash(string filePath)
    {
        var normalized = NormalizePath(filePath);
        return _originalHashes.TryGetValue(normalized, out var hash) ? hash : null;
    }

    /// <summary>Check if a file was modified by comparing to original hash.</summary>
    public bool WasModified(string filePath)
    {
        var normalized = NormalizePath(filePath);
        if (!_originalHashes.TryGetValue(normalized, out var original))
            return false;

        var current = ComputeFileHash(filePath);
        return original != current;
    }

    /// <summary>Get all modifications to a file.</summary>
    public IEnumerable<ModificationInfo> GetModifications(string filePath)
    {
        var normalized = NormalizePath(filePath);
        return _modifications.Where(m => m.FilePath == normalized);
    }

    /// <summary>Detect conflicts: multiple participants modified the same file.</summary>
    public IEnumerable<(string filePath, List<string> participants)> DetectConflicts()
    {
        return _modifications
            .GroupBy(m => m.FilePath)
            .Where(g => g.Select(m => m.ParticipantName).Distinct().Count() > 1)
            .Select(g => (g.Key, g.Select(m => m.ParticipantName).Distinct().ToList()));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NormalizePath(string filePath)
        => Path.GetFullPath(filePath).ToLowerInvariant();

    private static string ComputeFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return "FILE_NOT_FOUND";

            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "HASH_ERROR";
        }
    }
}
