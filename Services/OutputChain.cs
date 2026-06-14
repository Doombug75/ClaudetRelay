using System;
using System.Collections.Generic;
using ClaudetRelay.Models;

namespace ClaudetRelay.Services;

/// <summary>
/// Timestamp-ordered chain of ASR results and noise commands.
///
/// Workers reserve a placeholder (Pending=true) when they start, fill it when done.
/// The chain processor drains from the front only when the head entry is no longer
/// pending — preserving the order in which recordings and noises actually began,
/// regardless of how long each takes to process.
///
/// Thread safety: all list mutations hold _lock. EntryReady fires outside the lock.
/// </summary>
public sealed class OutputChain
{
    /// <summary>
    /// Fired (on the calling thread — marshal to UI if needed) when the head entry
    /// is ready and has been removed from the chain.
    /// </summary>
    public event Action<OutputChainEntry>? EntryReady;

    private readonly LinkedList<OutputChainEntry> _list = new();
    private readonly object _lock = new();

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reserve a placeholder for a batch-ASR job.
    /// Call on the audio thread when recording starts; the returned entry must be
    /// passed through the work queue so the worker can call Complete() on it.
    /// </summary>
    public OutputChainEntry Reserve(DateTime timestamp)
    {
        var entry = new OutputChainEntry(timestamp);
        lock (_lock) InsertSorted(entry);
        return entry;
    }

    /// <summary>
    /// Complete a previously reserved ASR entry and drain any newly-unblocked entries.
    /// Call from the worker thread when transcription finishes (even if text is empty).
    /// </summary>
    public void Complete(OutputChainEntry entry, string text)
    {
        entry.Complete(text);
        DrainFront();
    }

    /// <summary>
    /// Add a noise command that is immediately ready (no async work needed).
    /// Call from the audio thread when a command fires.
    /// </summary>
    public void AddCommand(DateTime timestamp, VoiceCommand command)
    {
        var entry = new OutputChainEntry(timestamp, command);
        lock (_lock) InsertSorted(entry);
        DrainFront();
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private void InsertSorted(OutputChainEntry entry)
    {
        // Walk from the tail; insert before the first entry whose timestamp is earlier
        var node = _list.Last;
        while (node != null && node.Value.Timestamp > entry.Timestamp)
            node = node.Previous;

        if (node == null)
            _list.AddFirst(entry);
        else
            _list.AddAfter(node, entry);
    }

    private void DrainFront()
    {
        while (true)
        {
            OutputChainEntry? front;
            lock (_lock)
            {
                if (_list.Count == 0 || _list.First!.Value.Pending) return;
                front = _list.First.Value;
                _list.RemoveFirst();
            }
            // Fire outside the lock so subscribers can call back into the chain
            EntryReady?.Invoke(front);
        }
    }
}
