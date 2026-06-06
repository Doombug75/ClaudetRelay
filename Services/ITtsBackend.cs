namespace ClaudetRelay.Services;

/// <summary>Display name + metadata for one TTS voice.</summary>
public record VoiceEntry(string DisplayName, string Language = "", int SpeakerId = 0);

/// <summary>
/// Contract for a TTS synthesis backend.
/// All backends return WAV bytes so <see cref="VoiceOutputService"/> can play them
/// uniformly via NAudio — no playback code in the backend itself.
/// </summary>
public interface ITtsBackend : IDisposable
{
    /// <summary>Short human-readable backend name (shown in Audio menu).</summary>
    string Name { get; }

    /// <summary>Returns all available voices/speakers. Must be synchronous-safe for UI calls.</summary>
    IReadOnlyList<VoiceEntry> GetVoices();

    /// <summary>
    /// Synthesizes <paramref name="text"/> and returns WAV audio bytes.
    /// Returns an empty array on failure — callers must handle gracefully.
    /// </summary>
    Task<byte[]> SynthesizeToWavAsync(
        string text,
        string voiceName,
        float  speed = 1.0f,
        CancellationToken ct = default);

    /// <summary>Returns true if the backend is ready to synthesize speech.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
