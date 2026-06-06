using System.Text.RegularExpressions;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace ClaudetRelay.Services;

/// <summary>
/// Speaks AI participant messages using the Windows TTS engine (WinRT SpeechSynthesizer).
/// Supports all installed voices including the modern Windows AI neural voices.
/// Thread-safe; always fire-and-forget — never throws to the caller.
/// </summary>
public static class VoiceOutputService
{
    private static MediaPlayer?            _player;
    private static SpeechSynthesisStream?  _stream;
    private static readonly object         _lock = new();

    // ── Voice discovery ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the display names of all installed TTS voices, sorted alphabetically.
    /// Returns an empty list if the WinRT speech API is unavailable.
    /// </summary>
    public static IReadOnlyList<string> GetVoiceNames()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .OrderBy(v => v.DisplayName)
                .Select(v => v.DisplayName)
                .ToList();
        }
        catch { return []; }
    }

    // ── Playback ───────────────────────────────────────────────────────────

    /// <summary>
    /// Speaks <paramref name="text"/> using the named voice.
    /// Stops any currently playing speech first.
    /// Fire-and-forget — returns immediately; never throws.
    /// </summary>
    public static async void SpeakAsync(string text, string voiceName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(voiceName)) return;

        try
        {
            StopCurrent();

            var cleanText = CleanForSpeech(text);
            if (string.IsNullOrWhiteSpace(cleanText)) return;

            using var synth = new SpeechSynthesizer();

            var voice = SpeechSynthesizer.AllVoices
                .FirstOrDefault(v => string.Equals(v.DisplayName, voiceName,
                                     StringComparison.OrdinalIgnoreCase));
            if (voice is not null)
                synth.Voice = voice;

            var stream = await synth.SynthesizeTextToStreamAsync(cleanText);

            lock (_lock)
            {
                // Dispose previous stream/player defensively
                DisposeCurrentLocked();

                _stream = stream;
                _player = new MediaPlayer();
                _player.Source = MediaSource.CreateFromStream(_stream, _stream.ContentType);
                _player.MediaEnded += (_, _) =>
                {
                    lock (_lock) { DisposeCurrentLocked(); }
                };
                _player.Play();
            }
        }
        catch { /* TTS is best-effort — any failure is silently ignored */ }
    }

    /// <summary>Stops and discards any currently playing speech immediately.</summary>
    public static void StopCurrent()
    {
        lock (_lock) { DisposeCurrentLocked(); }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void DisposeCurrentLocked()
    {
        if (_player is not null)
        {
            try { _player.Pause(); _player.Dispose(); } catch { }
            _player = null;
        }
        if (_stream is not null)
        {
            try { _stream.Dispose(); } catch { }
            _stream = null;
        }
    }

    /// <summary>
    /// Strips markdown and internal tags so the TTS engine reads clean prose.
    /// Also truncates very long messages so playback stays snappy.
    /// </summary>
    internal static string CleanForSpeech(string text)
    {
        var s = text;

        // Strip fenced code blocks entirely (they sound terrible when read aloud)
        s = Regex.Replace(s, @"```[\s\S]*?```", "code block.", RegexOptions.None);
        // Strip inline code
        s = Regex.Replace(s, @"`[^`\n]+`", "");
        // Strip internal ClaudetRelay output/roadmap tags
        s = Regex.Replace(s, @"<output[^>]*>[\s\S]*?</output>",  "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<projectplan[^>]*>[\s\S]*?</projectplan>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<roadmapproposal>[\s\S]*?</roadmapproposal>", "", RegexOptions.IgnoreCase);
        // Strip markdown headings
        s = Regex.Replace(s, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        // Strip bold / italic markers (keep inner text)
        s = Regex.Replace(s, @"\*{1,3}([^*\n]+)\*{1,3}", "$1");
        s = Regex.Replace(s, @"_{1,3}([^_\n]+)_{1,3}", "$1");
        // Strip links — keep display text
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        // Strip remaining HTML-like tags
        s = Regex.Replace(s, @"<[^>]{1,60}>", " ");
        // Collapse excessive whitespace
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        // Hard cap at ~700 chars so playback doesn't run for minutes
        if (s.Length > 700) s = s[..697] + "...";

        return s.Trim();
    }
}
