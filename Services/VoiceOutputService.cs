using System.Text.RegularExpressions;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace ClaudetRelay.Services;

/// <summary>
/// Speaks text using the Windows TTS engine (WinRT SpeechSynthesizer).
/// Supports all installed voices including Windows AI neural voices.
///
/// Queue behaviour is controlled by the <c>interrupt</c> parameter on
/// <see cref="Enqueue"/>:
///   interrupt = true  → new message cancels current speech and clears the queue
///   interrupt = false → new message is appended; messages play one after another
///
/// Thread-safe. Raises <see cref="StateChanged"/> whenever playback state changes;
/// subscribers are responsible for dispatching to their own UI thread.
/// Not coupled to any settings or UI framework — portable to other projects.
/// </summary>
public static class VoiceOutputService
{
    // ── State ──────────────────────────────────────────────────────────────

    private static readonly object   _lock   = new();
    private static MediaPlayer?            _player;
    private static SpeechSynthesisStream?  _stream;
    private static TaskCompletionSource?   _playTcs;      // set when playback finishes / is cancelled
    private static readonly Queue<(string Text, string VoiceName)> _queue = new();
    private static bool                    _isPlaying;

    // ── Public surface ─────────────────────────────────────────────────────

    /// <summary>True while a message is actively playing through the audio device.</summary>
    public static bool IsPlaying  { get { lock (_lock) return _isPlaying; } }

    /// <summary>Number of messages waiting in the queue (not counting the one playing).</summary>
    public static int  QueueCount { get { lock (_lock) return _queue.Count; } }

    /// <summary>
    /// Raised whenever playback starts, ends, is skipped, or the queue empties.
    /// Not dispatched to any specific thread — subscribe carefully from UI code.
    /// </summary>
    public static event Action? StateChanged;

    /// <summary>Returns the display names of all installed TTS voices, sorted alphabetically.</summary>
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

    /// <summary>
    /// Adds <paramref name="text"/> to the playback queue.
    /// If <paramref name="interrupt"/> is true the queue is cleared and current speech
    /// is stopped before the new message is added.
    /// The text should already be cleaned / truncated by the caller via
    /// <see cref="CleanForSpeech"/> so this service stays decoupled from app settings.
    /// </summary>
    public static void Enqueue(string text, string voiceName, bool interrupt)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(voiceName)) return;

        bool needsStart;
        lock (_lock)
        {
            if (interrupt)
            {
                _queue.Clear();
                CancelCurrentLocked();   // signal _playTcs → processor loop advances
            }
            _queue.Enqueue((text, voiceName));
            needsStart = !_isPlaying;
        }

        if (needsStart) _ = ProcessQueueAsync();
    }

    /// <summary>
    /// Skips the currently playing message and immediately starts the next one
    /// (if any). Does nothing when nothing is playing.
    /// </summary>
    public static void Skip()
    {
        lock (_lock) { CancelCurrentLocked(); }
        // ProcessQueueAsync is already running and will pick up the next item.
    }

    /// <summary>Stops playback immediately and clears all queued messages.</summary>
    public static void StopAll()
    {
        lock (_lock)
        {
            _queue.Clear();
            CancelCurrentLocked();
        }
        StateChanged?.Invoke();
    }

    // ── Queue processor ────────────────────────────────────────────────────

    private static async Task ProcessQueueAsync()
    {
        while (true)
        {
            (string text, string voiceName) item;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out item))
                {
                    _isPlaying = false;
                    break;
                }
                _isPlaying = true;
            }
            StateChanged?.Invoke();

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) { _playTcs = tcs; }

            await PlayItemAsync(item.text, item.voiceName, tcs);

            lock (_lock)
            {
                _playTcs = null;
                DisposeCurrentLocked();
            }
        }

        StateChanged?.Invoke();   // queue empty
    }

    private static async Task PlayItemAsync(string text, string voiceName, TaskCompletionSource tcs)
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                string.Equals(v.DisplayName, voiceName, StringComparison.OrdinalIgnoreCase));
            if (voice is not null) synth.Voice = voice;

            var stream = await synth.SynthesizeTextToStreamAsync(text);

            // Bail out if already cancelled while synthesising
            if (tcs.Task.IsCompleted) { stream.Dispose(); return; }

            lock (_lock)
            {
                if (tcs.Task.IsCompleted) { stream.Dispose(); return; }
                DisposeCurrentLocked();
                _stream = stream;
                _player = new MediaPlayer();
                _player.Source = MediaSource.CreateFromStream(_stream, _stream.ContentType);
                _player.MediaEnded  += (_, _) => tcs.TrySetResult();
                _player.MediaFailed += (_, _) => tcs.TrySetResult();
                _player.Play();
            }

            await tcs.Task;
        }
        catch { tcs.TrySetResult(); }
        finally
        {
            lock (_lock) { DisposeCurrentLocked(); }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void CancelCurrentLocked()
    {
        _playTcs?.TrySetResult();  // unblocks PlayItemAsync's await tcs.Task
        DisposeCurrentLocked();
    }

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
    /// Strips markdown, code blocks, internal tags and truncates to <paramref name="maxChars"/>
    /// so speech playback is clean and doesn't run forever.
    /// Call this before passing text to <see cref="Enqueue"/> so the service itself stays
    /// decoupled from application settings.
    /// </summary>
    public static string CleanForSpeech(string text, int maxChars = 700)
    {
        var s = text;
        // Strip fenced code blocks
        s = Regex.Replace(s, @"```[\s\S]*?```", "code block.");
        // Strip inline code
        s = Regex.Replace(s, @"`[^`\n]+`", "");
        // Strip internal ClaudetRelay output/plan/roadmap tags
        s = Regex.Replace(s, @"<output[^>]*>[\s\S]*?</output>",  "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<projectplan[^>]*>[\s\S]*?</projectplan>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<roadmapproposal>[\s\S]*?</roadmapproposal>", "", RegexOptions.IgnoreCase);
        // Strip markdown headings
        s = Regex.Replace(s, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        // Strip bold/italic (keep text)
        s = Regex.Replace(s, @"\*{1,3}([^*\n]+)\*{1,3}", "$1");
        s = Regex.Replace(s, @"_{1,3}([^_\n]+)_{1,3}", "$1");
        // Strip links — keep display text
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        // Strip remaining HTML-like tags
        s = Regex.Replace(s, @"<[^>]{1,60}>", " ");
        // Normalise whitespace
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        // Truncate
        var cap = Math.Max(50, maxChars);
        if (s.Length > cap) s = s[..( cap - 1)] + "…";
        return s.Trim();
    }
}
