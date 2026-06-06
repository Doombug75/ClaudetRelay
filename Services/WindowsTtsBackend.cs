using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace ClaudetRelay.Services;

/// <summary>
/// TTS backend that uses the built-in Windows speech engine (WinRT SpeechSynthesizer).
/// Supports all installed voices including Windows AI neural voices.
/// Zero setup — always available on Windows 10 1803+.
/// </summary>
public sealed class WindowsTtsBackend : ITtsBackend
{
    public string Name => "Windows TTS";

    public IReadOnlyList<VoiceEntry> GetVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .OrderBy(v => v.DisplayName)
                .Select(v => new VoiceEntry(v.DisplayName, v.Language))
                .ToList();
        }
        catch { return []; }
    }

    public async Task<byte[]> SynthesizeToWavAsync(
        string text,
        string voiceName,
        float  speed = 1.0f,
        CancellationToken ct = default)
    {
        using var synth = new SpeechSynthesizer();

        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
            string.Equals(v.DisplayName, voiceName, StringComparison.OrdinalIgnoreCase));
        if (voice is not null) synth.Voice = voice;

        // Speed is approximated via SpeechSynthesisStream — WinRT doesn't expose rate directly,
        // but the returned WAV can be resampled by the caller if needed. For now we ignore speed.

        var stream = await synth.SynthesizeTextToStreamAsync(text);

        using var dataReader = new DataReader(stream.GetInputStreamAt(0));
        var size  = (uint)stream.Size;
        await dataReader.LoadAsync(size);
        var bytes = new byte[size];
        dataReader.ReadBytes(bytes);
        stream.Dispose();
        return bytes;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try   { return Task.FromResult(SpeechSynthesizer.AllVoices.Count > 0); }
        catch { return Task.FromResult(false); }
    }

    public void Dispose() { }
}
