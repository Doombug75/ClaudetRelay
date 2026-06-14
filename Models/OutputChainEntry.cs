using System;

namespace ClaudetRelay.Models;

public enum ChainEntryKind { Text, Command }

/// <summary>
/// One element in the output chain — either a transcribed text segment or a noise command.
/// Created (with Pending=true) when work starts; completed when the result is ready.
/// Ordered by Timestamp (recording/noise start time, UTC, ms precision).
/// </summary>
public sealed class OutputChainEntry
{
    public readonly DateTime       Timestamp;
    public readonly ChainEntryKind Kind;

    // Text entry: null until Complete() is called
    public string?       Text    { get; private set; }

    // Command entry: set at construction, never null for Kind==Command
    public VoiceCommand? Command { get; private set; }

    // Volatile so DrainFront sees the write from a worker thread without a lock
    public volatile bool Pending;

    /// <summary>Placeholder for a batch-ASR job. Pending=true until Complete() is called.</summary>
    public OutputChainEntry(DateTime timestamp)
    {
        Timestamp = timestamp;
        Kind      = ChainEntryKind.Text;
        Pending   = true;
    }

    /// <summary>Immediately-ready command entry.</summary>
    public OutputChainEntry(DateTime timestamp, VoiceCommand command)
    {
        Timestamp = timestamp;
        Kind      = ChainEntryKind.Command;
        Command   = command;
        Pending   = false;
    }

    public void Complete(string text)
    {
        Text    = text;
        Pending = false;
    }
}
