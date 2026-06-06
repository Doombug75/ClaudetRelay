using System.Text.RegularExpressions;
using NAudio.Wave;

namespace ClaudetRelay.Services;

/// <summary>
/// Speaks text using an <see cref="ITtsBackend"/> (Windows TTS, Sherpa-onnx, or VOICEVOX).
/// Playback is handled via NAudio WaveOutEvent so all backends share one code path.
///
/// Queue behaviour is controlled by the <c>interrupt</c> parameter on <see cref="Enqueue"/>:
///   interrupt = true  → new message cancels current speech and clears the queue
///   interrupt = false → new message is appended; messages play one after another
///
/// Thread-safe. Raises <see cref="StateChanged"/> whenever playback state changes;
/// subscribers are responsible for dispatching to their own UI thread.
/// Not coupled to any settings or UI framework — portable to other projects.
/// </summary>
public static class VoiceOutputService
{
    // ── Hardware: output device + volume ──────────────────────────────────

    /// <summary>
    /// NAudio device number (0-based). 0 = OS default. Applied to each new WaveOutEvent.
    /// </summary>
    public static int DeviceNumber { get; set; } = 0;

    private static float _volume = 1.0f;

    /// <summary>
    /// Master volume (0.0–1.0). Applies immediately to any currently playing stream
    /// as well as all future streams.
    /// </summary>
    public static float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_lock)
            {
                try { if (_waveOut is not null) _waveOut.Volume = _volume; } catch { }
            }
        }
    }

    // ── Backend ────────────────────────────────────────────────────────────

    private static ITtsBackend _backend = new WindowsTtsBackend();

    /// <summary>
    /// The currently active TTS backend.
    /// Setting a new value stops all current playback and disposes the old backend.
    /// </summary>
    public static ITtsBackend ActiveBackend
    {
        get => _backend;
        set
        {
            StopAll();
            var old = _backend;
            _backend = value;
            old.Dispose();
            StateChanged?.Invoke();
        }
    }

    // ── State ──────────────────────────────────────────────────────────────

    private static readonly object   _lock    = new();
    private static WaveOutEvent?      _waveOut;
    private static TaskCompletionSource? _playTcs;
    private static readonly Queue<(string Text, string VoiceName, float Speed)> _queue = new();
    private static bool               _isPlaying;

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

    /// <summary>Returns the display names of all voices from the active backend.</summary>
    public static IReadOnlyList<string> GetVoiceNames() =>
        _backend.GetVoices().Select(v => v.DisplayName).ToList();

    /// <summary>
    /// Returns all <see cref="VoiceEntry"/> objects from the active backend,
    /// including language and speaker-ID metadata.
    /// </summary>
    public static IReadOnlyList<VoiceEntry> GetVoiceEntries() =>
        _backend.GetVoices();

    /// <summary>
    /// Adds <paramref name="text"/> to the playback queue.
    /// If <paramref name="interrupt"/> is true the queue is cleared and current speech
    /// is stopped before the new message is added.
    /// </summary>
    public static void Enqueue(string text, string voiceName, bool interrupt, float speed = 1.0f)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(voiceName)) return;

        bool needsStart;
        lock (_lock)
        {
            if (interrupt)
            {
                _queue.Clear();
                CancelCurrentLocked();
            }
            _queue.Enqueue((text, voiceName, speed));
            needsStart = !_isPlaying;
        }

        if (needsStart) _ = ProcessQueueAsync();
    }

    /// <summary>Skips the currently playing message and starts the next one (if any).</summary>
    public static void Skip()
    {
        lock (_lock) { CancelCurrentLocked(); }
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
            (string text, string voiceName, float speed) item;
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

            await PlayItemAsync(item.text, item.voiceName, item.speed, tcs);

            lock (_lock) { _playTcs = null; }
        }

        StateChanged?.Invoke();   // queue empty
    }

    private static async Task PlayItemAsync(
        string text, string voiceName, float speed, TaskCompletionSource tcs)
    {
        WaveOutEvent?   waveOut    = null;
        WaveFileReader? waveReader = null;
        System.IO.MemoryStream? ms = null;

        try
        {
            var bytes = await _backend.SynthesizeToWavAsync(text, voiceName, speed);
            if (bytes.Length == 0 || tcs.Task.IsCompleted) { tcs.TrySetResult(); return; }

            ms         = new System.IO.MemoryStream(bytes);
            waveReader = new WaveFileReader(ms);
            waveOut    = new WaveOutEvent { DeviceNumber = DeviceNumber };

            lock (_lock)
            {
                if (tcs.Task.IsCompleted) { tcs.TrySetResult(); return; }
                DisposeCurrentLocked();
                _waveOut = waveOut;
            }

            waveOut.Init(waveReader);
            try { waveOut.Volume = Volume; } catch { }
            waveOut.PlaybackStopped += (_, _) => tcs.TrySetResult();
            waveOut.Play();

            await tcs.Task;
        }
        catch { tcs.TrySetResult(); }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_waveOut, waveOut)) _waveOut = null;
            }
            try { waveOut?.Stop();    waveOut?.Dispose();    } catch { }
            try { waveReader?.Dispose();                     } catch { }
            try { ms?.Dispose();                             } catch { }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void CancelCurrentLocked()
    {
        _playTcs?.TrySetResult();   // unblocks PlayItemAsync's await tcs.Task
        DisposeCurrentLocked();
    }

    private static void DisposeCurrentLocked()
    {
        if (_waveOut is not null)
        {
            try { _waveOut.Stop(); _waveOut.Dispose(); } catch { }
            _waveOut = null;
        }
    }

    /// <summary>
    /// Strips markdown, code blocks, internal tags and truncates to <paramref name="maxChars"/>
    /// so speech playback is clean and doesn't run forever.
    /// </summary>
    public static string CleanForSpeech(string text, int maxChars = 700)
    {
        var s = text;
        s = Regex.Replace(s, @"```[\s\S]*?```", "code block.");
        s = Regex.Replace(s, @"`[^`\n]+`", "");
        s = Regex.Replace(s, @"<output[^>]*>[\s\S]*?</output>",               "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<projectplan[^>]*>[\s\S]*?</projectplan>",      "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<roadmapproposal>[\s\S]*?</roadmapproposal>",   "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^#{1,6}\s+",           "",  RegexOptions.Multiline);
        s = Regex.Replace(s, @"\*{1,3}([^*\n]+)\*{1,3}", "$1");
        s = Regex.Replace(s, @"_{1,3}([^_\n]+)_{1,3}",   "$1");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        s = Regex.Replace(s, @"<[^>]{1,60}>", " ");
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");

        var cap = Math.Max(50, maxChars);
        if (s.Length > cap) s = s[..(cap - 1)] + "…";
        return s.Trim();
    }
}
