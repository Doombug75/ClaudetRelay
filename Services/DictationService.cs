using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.Wave;
using SherpaOnnx;
using ClaudetRelay.Models;

namespace ClaudetRelay.Services;

public enum DictationActivationMode { AlwaysOn, PushToTalk }
public enum DictationState         { Idle, Listening, Recording, Processing }

/// <summary>
/// Records microphone audio via NAudio and transcribes it using a
/// locally-loaded SherpaOnnx offline ASR model (Whisper or SenseVoice)
/// or a streaming online ASR model (Zipformer Transducer, Paraformer, Zipformer2 CTC).
///
/// Batch mode (Whisper / SenseVoice):
///   Audio is accumulated until silence / PTT release, then sent to the
///   offline recogniser in one shot. TextAvailable fires once per utterance.
///
/// Streaming mode (Zipformer / Paraformer / Zipformer2CTC):
///   Audio is fed to the online recogniser chunk-by-chunk as it arrives.
///   The recogniser emits a partial result at every natural phrase endpoint
///   (configured via Rule1/2/3 silence thresholds). TextAvailable fires once
///   per phrase — text appears progressively while the mic stays open.
///
/// Two activation modes (both batch and streaming):
///   AlwaysOn   — hands-free: recording starts automatically when RMS exceeds the
///                configured threshold and stops after a period of silence; the mic
///                stays open and re-arms for the next utterance.
///   PushToTalk — call PttDown()/PttUp() to control recording.
/// </summary>
public sealed class DictationService : IDisposable
{
    // ── Events ─────────────────────────────────────────────────────────────
    /// <summary>Fired ~10 times/second with current microphone RMS level (0–1).</summary>
    public event Action<float>?          LevelChanged;
    /// <summary>
    /// Fired when a transcription result is available.
    /// In batch mode: once per utterance after silence/PTT.
    /// In streaming mode: once per detected phrase endpoint (progressive).
    /// </summary>
    public event Action<string>?         TextAvailable;
    /// <summary>Fired whenever the service state changes.</summary>
    public event Action<DictationState>? StateChanged;
    /// <summary>Fired with every raw PCM chunk from the microphone (16 kHz mono float). Used by noise command matching.</summary>
    public event Action<float[]>? RawAudioAvailable;
    /// <summary>Fired each time any ASR job completes, even when the result is empty. Used to detect when the worker queue fully drains.</summary>
    [Obsolete("Subscribe to OutputChain.EntryReady instead.")]
    public event Action? AnyTranscriptionCompleted;

    /// <summary>Fired whenever the number of in-flight ASR jobs changes (submitted or
    /// completed). The argument is the current <see cref="PendingTranscriptionCount"/>.
    /// Lets the UI show a busy/working indicator while transcription is calculating.</summary>
    public event Action<int>? PendingCountChanged;

    // ── Config ─────────────────────────────────────────────────────────────
    private DictationActivationMode _mode         = DictationActivationMode.AlwaysOn;
    private float                   _threshold    = 0.04f;
    private int                     _deviceNumber = 0;
    private int                     _silenceMs    = 1500;

    // ── Runtime state ──────────────────────────────────────────────────────

    /// <summary>
    /// Microphone gain multiplier. 1.0 = no boost, 2.0 = double, 3.0 = triple.
    /// Applied to raw samples before RMS calculation and before passing to the recogniser.
    /// </summary>
    public float MicBoost { get; set; } = 1.0f;

    /// <summary>True when a streaming online recogniser is loaded (Zipformer / Paraformer / Zipformer2CTC).</summary>
    public bool IsStreamingMode => _onlineRecognizer is not null;

    private DictationState     _state          = DictationState.Idle;
    private WaveInEvent?       _waveIn;

    // ── Output chain (batch mode only) ────────────────────────────────────
    public OutputChain? OutputChain { get; set; }

    // ── Batch mode state ───────────────────────────────────────────────────
    private List<float>        _samples        = new();
    private bool               _recording      = false;
    private bool               _voiceTriggered = false;
    private bool               _pausedByUser   = false;
    private int                _silenceSamples = 0;
    private int                _postNoiseSuppress = 0; // samples remaining where re-arm is blocked
    private DateTime           _recordingStartTime;
    private OutputChainEntry?  _currentChainEntry;

    // ── Batch recogniser ───────────────────────────────────────────────────
    private OfflineRecognizer? _recognizer;
    private readonly object    _recognizerLock = new();  // guards Decode() — OfflineRecognizer is not thread-safe for parallel decodes

    // ── Pipeline ordering ──────────────────────────────────────────────────
    private int _captureSeq = 0;
    private int _emitSeq    = 0;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _pendingResults = new();

    // ── Pre-warmed worker pool ─────────────────────────────────────────────
    private System.Collections.Concurrent.BlockingCollection<(int seq, float[] samples, OutputChainEntry? chainEntry)>? _workQueue;
    private Thread[]? _workers;
    private const int WorkerCount = 3;

    // ── Streaming recogniser ───────────────────────────────────────────────
    private OnlineRecognizer?  _onlineRecognizer;
    private OnlineStream?      _onlineStream;
    private readonly object    _onlineLock     = new();

    private const int   SampleRate    = 16000;
    private const float SilenceFactor = 0.5f;   // silence threshold = threshold * factor

    public bool           IsActive    => _state != DictationState.Idle || _recording;
    public bool           IsRecording => _recording;
    public DictationState State       => _state;

    /// <summary>
    /// Call this immediately on the audio thread when a noise command fires, before
    /// the handler is dispatched to the UI thread. Sets the post-noise suppress counter
    /// so the very next audio batch doesn't re-arm recording from the resonance tail.
    /// </summary>
    public void SuppressRearm() => _postNoiseSuppress = SampleRate / 6;
    public bool           IsModelLoaded { get { lock (_recognizerLock) return _recognizer is not null; } }

    /// <summary>Number of ASR jobs queued but not yet emitted via TextAvailable.</summary>
    public int PendingTranscriptionCount =>
        Math.Max(0, System.Threading.Volatile.Read(ref _captureSeq)
                   - System.Threading.Volatile.Read(ref _emitSeq));

    /// <summary>
    /// Runs <paramref name="samples"/> through the loaded offline model and returns
    /// the recognised text, or null if no model is loaded.
    /// Safe to call from any thread.
    /// </summary>
    public Task<string?> TranscribeSampleAsync(float[] samples) => Task.Run(() =>
    {
        OfflineRecognizer? recognizer;
        lock (_recognizerLock) { recognizer = _recognizer; }
        if (recognizer is null) return null;

        // Pad with 300 ms of silence after the clip so Whisper has a clear end-of-speech
        // signal and properly closes parenthetical tokens like "(farting)".
        var padded = new float[samples.Length + SampleRate / 3];
        samples.CopyTo(padded, 0);

        SherpaOnnx.OfflineStream stream;
        lock (_recognizerLock)
        {
            stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, padded);
            recognizer.Decode(stream);
        }
        var text = stream.Result.Text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    });

    // ── Model loading ──────────────────────────────────────────────────────

    /// <summary>
    /// Loads an ASR model from <paramref name="modelFolder"/>.
    /// <paramref name="modelType"/> selects the engine:
    ///   "whisper"      — batch, Whisper encoder+decoder+tokens
    ///   "sense_voice"  — batch, SenseVoice model+tokens
    ///   "zipformer"    — streaming, Transducer encoder+decoder+joiner
    ///   "paraformer"   — streaming, Paraformer encoder+decoder
    ///   "zipformer2ctc"— streaming, Zipformer2-CTC single model file
    /// Returns true on success.
    /// </summary>
    public bool LoadModel(string modelType, string modelFolder)
    {
        try
        {
            switch (modelType.ToLowerInvariant())
            {
                case "whisper":
                case "sense_voice":
                    return LoadBatchModel(modelType, modelFolder);

                case "zipformer":
                case "paraformer":
                case "zipformer2ctc":
                    return LoadStreamingModel(modelType, modelFolder);

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DictationService.LoadModel error: {ex.Message}");
            return false;
        }
    }

    private bool LoadBatchModel(string modelType, string modelFolder)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Debug      = 0;
        config.ModelConfig.NumThreads = Math.Max(2, Environment.ProcessorCount / 2);

        switch (modelType.ToLowerInvariant())
        {
            case "whisper":
            {
                var enc = FindFile(modelFolder, "*encoder*.onnx");
                var dec = FindFile(modelFolder, "*decoder*.onnx");
                var tok = FindFile(modelFolder, "*tokens.txt");
                if (enc is null || dec is null || tok is null) return false;
                config.ModelConfig.Tokens          = tok;
                config.ModelConfig.Whisper.Encoder = enc;
                config.ModelConfig.Whisper.Decoder = dec;
                break;
            }
            case "sense_voice":
            {
                var mdl = FindFile(modelFolder, "model*.onnx");
                var tok = FindFile(modelFolder, "tokens.txt");
                if (mdl is null || tok is null) return false;
                config.ModelConfig.Tokens = tok;
                config.ModelConfig.SenseVoice.Model = mdl;
                config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                break;
            }
            default:
                return false;
        }

        // Dispose streaming recogniser if one was previously loaded
        DisposeOnlineRecognizer();

        OfflineRecognizer? old;
        var fresh = new OfflineRecognizer(config);
        lock (_recognizerLock)
        {
            old         = _recognizer;
            _recognizer = fresh;
        }
        old?.Dispose();

        // Pre-warm: run a silent dummy inference so the ONNX graph is JIT-compiled
        // before the user speaks — eliminates the 1-2s lag on first real utterance
        Task.Run(() =>
        {
            try
            {
                var dummy = new float[SampleRate / 2]; // 0.5s of silence
                lock (_recognizerLock)
                {
                    if (_recognizer != fresh) return;
                    var s = fresh.CreateStream();
                    s.AcceptWaveform(SampleRate, dummy);
                    fresh.Decode(s);
                }
            }
            catch { }
        });

        StartWorkerPool();
        return true;
    }

    private void StartWorkerPool()
    {
        StopWorkerPool();
        _workQueue = new System.Collections.Concurrent.BlockingCollection<(int, float[], OutputChainEntry?)>(boundedCapacity: 32);
        _workers   = new Thread[WorkerCount];
        for (int i = 0; i < WorkerCount; i++)
        {
            var t = new Thread(WorkerLoop) { IsBackground = true, Name = $"AsrWorker-{i}" };
            _workers[i] = t;
            t.Start();
        }
    }

    private void StopWorkerPool()
    {
        _workQueue?.CompleteAdding();
        // Workers are IsBackground — they die with the process.
        // No Join here: blocking the UI thread while a Whisper decode finishes
        // makes VS think the app is still running for up to 15s.
        _workQueue?.Dispose();
        _workQueue = null;
        _workers   = null;
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var (seq, samples, chainEntry) in _workQueue!.GetConsumingEnumerable())
            {
                string text = "";
                try
                {
                    OfflineRecognizer? recognizer;
                    lock (_recognizerLock) { recognizer = _recognizer; }
                    if (recognizer is null) { _pendingResults[seq] = ""; DrainPendingResults(); chainEntry?.Complete(""); OutputChain?.Complete(chainEntry!, ""); continue; }

                    SherpaOnnx.OfflineStream stream;
                    lock (_recognizerLock)
                    {
                        stream = recognizer.CreateStream();
                        stream.AcceptWaveform(SampleRate, samples);
                        recognizer.Decode(stream);
                        text = stream.Result.Text.Trim();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AsrWorker transcribe error: {ex.Message}");
                }
                _pendingResults[seq] = text;
                DrainPendingResults();
                // Complete chain entry — fires EntryReady when this is the front and unblocked
                if (chainEntry != null)
                    OutputChain?.Complete(chainEntry, text);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException)    { }
    }

    private bool LoadStreamingModel(string modelType, string modelFolder)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate  = SampleRate;
        config.FeatConfig.FeatureDim  = 80;
        config.ModelConfig.NumThreads = Math.Max(2, Environment.ProcessorCount / 2);
        config.ModelConfig.Debug      = 0;
        config.DecodingMethod         = "greedy_search";

        // Endpoint detection — fires TextAvailable at natural phrase breaks
        config.EnableEndpoint            = 1;
        config.Rule1MinTrailingSilence   = 2.4f;  // trailing silence → endpoint
        config.Rule2MinTrailingSilence   = 1.2f;  // silence after prior endpoint
        config.Rule3MinUtteranceLength   = 20.0f; // minimum utterance before endpoint

        switch (modelType.ToLowerInvariant())
        {
            case "zipformer":
            {
                var enc    = FindFile(modelFolder, "*encoder*.onnx");
                var dec    = FindFile(modelFolder, "*decoder*.onnx");
                var joiner = FindFile(modelFolder, "*joiner*.onnx");
                var tok    = FindFile(modelFolder, "tokens.txt");
                if (enc is null || dec is null || joiner is null || tok is null) return false;
                config.ModelConfig.Tokens                     = tok;
                config.ModelConfig.Transducer.Encoder         = enc;
                config.ModelConfig.Transducer.Decoder         = dec;
                config.ModelConfig.Transducer.Joiner          = joiner;
                break;
            }
            case "paraformer":
            {
                var enc = FindFile(modelFolder, "*encoder*.onnx");
                var dec = FindFile(modelFolder, "*decoder*.onnx");
                var tok = FindFile(modelFolder, "tokens.txt");
                if (enc is null || dec is null || tok is null) return false;
                config.ModelConfig.Tokens              = tok;
                config.ModelConfig.Paraformer.Encoder  = enc;
                config.ModelConfig.Paraformer.Decoder  = dec;
                break;
            }
            case "zipformer2ctc":
            {
                var mdl = FindFile(modelFolder, "model*.onnx");
                var tok = FindFile(modelFolder, "tokens.txt");
                if (mdl is null || tok is null) return false;
                config.ModelConfig.Tokens                  = tok;
                config.ModelConfig.Zipformer2Ctc.Model     = mdl;
                break;
            }
            default:
                return false;
        }

        // Dispose batch recogniser if one was previously loaded
        lock (_recognizerLock)
        {
            var old = _recognizer;
            _recognizer = null;
            old?.Dispose();
        }

        OnlineRecognizer?  oldOnline;
        OnlineStream?      oldStream;
        var freshRecognizer = new OnlineRecognizer(config);
        var freshStream     = freshRecognizer.CreateStream();
        lock (_onlineLock)
        {
            oldOnline          = _onlineRecognizer;
            oldStream          = _onlineStream;
            _onlineRecognizer  = freshRecognizer;
            _onlineStream      = freshStream;
        }
        oldStream?.Dispose();
        oldOnline?.Dispose();
        return true;
    }

    /// <summary>
    /// Infers ASR model type from the folder name using sherpa-onnx naming conventions.
    /// Returns the type key ("whisper", "sense_voice", "zipformer", "paraformer", "zipformer2ctc")
    /// or null if the type cannot be determined.
    /// </summary>
    public static string? DetectModelTypeFromFolder(string folder)
    {
        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')).ToLowerInvariant();
        if (name.Contains("whisper"))       return "whisper";
        if (name.Contains("sense_voice") || name.Contains("sense-voice") || name.Contains("sensevoice")) return "sense_voice";
        if (name.Contains("zipformer2ctc") || name.Contains("zipformer-2-ctc") || name.Contains("zipformer2_ctc")) return "zipformer2ctc";
        if (name.Contains("zipformer"))     return "zipformer";
        if (name.Contains("paraformer"))    return "paraformer";
        return null;
    }

    private static string? FindFile(string folder, string pattern)
    {
        var matches = System.IO.Directory.GetFiles(folder, pattern);
        return matches.Length > 0 ? matches[0] : null;
    }

    // ── Activation ─────────────────────────────────────────────────────────

    public void Configure(DictationActivationMode mode, float threshold, int deviceNumber, int silenceMs = 1500)
    {
        _mode         = mode;
        _threshold    = Math.Max(0.001f, threshold);
        _deviceNumber = deviceNumber;
        _silenceMs    = Math.Clamp(silenceMs, 300, 5000);
    }

    /// <summary>
    /// Start the microphone capture and enter the configured mode.
    /// Pass <paramref name="startRecordingChunk"/> = false to open the mic for level
    /// monitoring / standby without immediately starting to accumulate samples —
    /// useful for pre-loading so the first button press is instant.
    /// </summary>
    public void Activate(bool startRecordingChunk = true)
    {
        if (_waveIn is not null) return;

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber       = _deviceNumber,
                WaveFormat         = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 80
            };
            _waveIn.DataAvailable += OnData;
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DictationService.Activate error: {ex.Message}");
            return;
        }

        if (_mode == DictationActivationMode.AlwaysOn && startRecordingChunk)
            BeginRecording();

        SetState(DictationState.Listening);
    }

    /// <summary>
    /// Starts accumulating a new recording chunk without reopening the mic.
    /// Use this when the mic is already open (pre-activated) and the user clicks
    /// the record button — response is instant.
    /// </summary>
    public void StartRecordingChunk()
    {
        if (_waveIn is null || _recording) return;
        _pausedByUser = false;
        BeginRecording();
        SetState(DictationState.Recording);
    }

    /// <summary>Stop capture and release the microphone.</summary>
    public void Deactivate()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnData;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }
        _recording      = false;
        _voiceTriggered = false;
        _pausedByUser   = false;
        _samples.Clear();
        _pendingResults.Clear();
        System.Threading.Volatile.Write(ref _captureSeq, 0);
        System.Threading.Volatile.Write(ref _emitSeq,    0);
        StopWorkerPool();

        // Flush any remaining streaming audio and emit a final result
        if (IsStreamingMode)
            FlushStreamingResult();

        SetState(DictationState.Idle);
    }

    // ── Push-to-talk ───────────────────────────────────────────────────────

    public void PttDown()
    {
        if (_mode != DictationActivationMode.PushToTalk) return;
        if (_waveIn is null || _recording) return;
        BeginRecording();
    }

    public void PttUp()
    {
        if (_mode != DictationActivationMode.PushToTalk) return;
        if (IsStreamingMode)
        {
            // In streaming mode PTT-up flushes whatever is in the stream
            FlushStreamingResult();
            SetState(_waveIn is not null ? DictationState.Listening : DictationState.Idle);
        }
        else
        {
            if (_recording) StopAndTranscribe();
        }
    }

    // ── Internal audio processing ──────────────────────────────────────────

    private void BeginRecording()
    {
        // Complete any orphaned chain entry from a previous recording that never got committed
        _currentChainEntry?.Complete("");

        _samples.Clear();
        _silenceSamples     = 0;
        _recording          = true;
        _recordingStartTime = DateTime.UtcNow;
        _currentChainEntry  = OutputChain?.Reserve(_recordingStartTime);
        SetState(DictationState.Recording);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var boost = MathF.Max(0f, MicBoost);

        // Convert buffer to float samples (with boost) and compute RMS
        int     count   = e.BytesRecorded / 2;
        float   sumSq   = 0;
        float[] samples = new float[count];
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            float f = MemoryMarshal.Read<short>(e.Buffer.AsSpan(i)) / 32768f * boost;
            samples[i / 2] = Math.Clamp(f, -1f, 1f);
            sumSq += f * f;
        }
        float rms = count > 0 ? MathF.Sqrt(sumSq / count) : 0f;
        LevelChanged?.Invoke(rms);
        RawAudioAvailable?.Invoke(samples);

        if (IsStreamingMode)
            OnDataStreaming(samples, rms);
        else
            OnDataBatch(e, samples, rms, count);
    }

    // ── Streaming path ─────────────────────────────────────────────────────

    private void OnDataStreaming(float[] samples, float rms)
    {
        // AlwaysOn (hands-free): wait for voice above threshold before starting
        if (_mode == DictationActivationMode.AlwaysOn)
        {
            if (_postNoiseSuppress > 0)
            {
                _postNoiseSuppress = Math.Max(0, _postNoiseSuppress - samples.Length);
                return;
            }
            if (!_voiceTriggered)
            {
                if (rms < _threshold) return;
                _voiceTriggered = true;
                _recording      = true;
                SetState(DictationState.Recording);
            }
        }

        // PushToTalk: only feed while recording flag is set
        if (_mode == DictationActivationMode.PushToTalk && !_recording)
            return;

        // Feed chunk to the online recogniser
        lock (_onlineLock)
        {
            if (_onlineRecognizer is null || _onlineStream is null) return;

            _onlineStream.AcceptWaveform(SampleRate, samples);

            while (_onlineRecognizer.IsReady(_onlineStream))
                _onlineRecognizer.Decode(_onlineStream);

            if (_onlineRecognizer.IsEndpoint(_onlineStream))
            {
                var result = _onlineRecognizer.GetResult(_onlineStream);
                var text   = result.Text.Trim();

                if (!string.IsNullOrWhiteSpace(text))
                    TextAvailable?.Invoke(text);

                _onlineRecognizer.Reset(_onlineStream);

                // In hands-free mode re-arm after each endpoint
                if (_mode == DictationActivationMode.AlwaysOn)
                    _voiceTriggered = false;
            }
        }
    }

    /// <summary>
    /// Signals end-of-input to the streaming recogniser, decodes any remaining
    /// audio, emits a final TextAvailable if there is leftover text, then resets
    /// the stream ready for the next utterance.
    /// </summary>
    private void FlushStreamingResult()
    {
        lock (_onlineLock)
        {
            if (_onlineRecognizer is null || _onlineStream is null) return;

            _onlineStream.InputFinished();
            while (_onlineRecognizer.IsReady(_onlineStream))
                _onlineRecognizer.Decode(_onlineStream);

            var result = _onlineRecognizer.GetResult(_onlineStream);
            var text   = result.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                TextAvailable?.Invoke(text);

            // Reset stream for next use (CreateStream allocates a new decoder state)
            _onlineStream.Dispose();
            _onlineStream = _onlineRecognizer.CreateStream();
        }
        _recording      = false;
        _voiceTriggered = false;
    }

    // ── Batch path ─────────────────────────────────────────────────────────

    private void OnDataBatch(WaveInEventArgs e, float[] samples, float rms, int count)
    {
        // AlwaysOn (hands-free): wait for voice above threshold before accumulating
        if (_mode == DictationActivationMode.AlwaysOn)
        {
            if (_postNoiseSuppress > 0)
            {
                _postNoiseSuppress = Math.Max(0, _postNoiseSuppress - count);
                return;
            }
            if (_waveIn is not null && !_voiceTriggered && !_pausedByUser && rms >= _threshold)
            {
                _voiceTriggered = true;
                BeginRecording();
            }
        }

        if (!_recording) return;

        _samples.AddRange(samples);

        // AlwaysOn (hands-free): auto-transcribe after silence
        if (_mode == DictationActivationMode.AlwaysOn)
        {
            if (rms < _threshold * SilenceFactor)
            {
                _silenceSamples += count;
                if (_silenceSamples >= SampleRate * _silenceMs / 1000)
                {
                    _voiceTriggered = false;
                    StopAndTranscribe();
                }
            }
            else
            {
                _silenceSamples = 0;
            }
        }
    }

    /// <summary>
    /// Stops the current batch recording chunk and sends it to the ASR engine.
    /// Use this when the mic button is clicked in AlwaysOn mode so the
    /// accumulated audio gets transcribed before the service is deactivated.
    /// Safe to call from any thread.
    /// </summary>
    public void FinalizeRecording() { _pausedByUser = true; StopAndTranscribe(); }

    /// <summary>
    /// Cuts the current recording at this point and commits whatever speech was
    /// accumulated so far to the ASR engine. In streaming mode, flushes the online
    /// stream. After this call the service immediately re-arms so speech that
    /// follows a noise command is captured without a gap.
    /// Call this when a noise command fires mid-dictation.
    /// </summary>
    /// <summary>
    /// Cuts the current batch recording and commits accumulated speech to the ASR worker.
    /// The output chain handles sequencing — the caller no longer needs to defer actions.
    /// </summary>
    public void CommitAndRearm(int trailingSamplesToDrop = 0)
    {
        if (IsStreamingMode)
        {
            _postNoiseSuppress = SampleRate / 6;
            FlushStreamingResult();
            SetState(_waveIn is not null ? DictationState.Listening : DictationState.Idle);
            return;
        }

        // Always suppress re-arm after a noise so the resonance tail doesn't
        // immediately re-trigger recording and get transcribed as "(clicking)" etc.
        _postNoiseSuppress = SampleRate / 6;

        if (!_recording) return;

        _voiceTriggered = false;
        if (trailingSamplesToDrop > 0 && _samples.Count > 0)
        {
            // Cap the trim: a command noise is always brief (snap/click/squeak ≤ ~0.4 s).
            // The matcher's clip can balloon up to 2.5 s if speech runs straight into the
            // noise with no gap — trimming that would eat the spoken words and leave an
            // empty clip. Capping guarantees real speech survives; any residual noise
            // sound is handled by NoiseFilterWords stripping on the transcript.
            int maxDrop = (int)(SampleRate * 0.4);
            int drop = Math.Min(_samples.Count, Math.Min(trailingSamplesToDrop, maxDrop));
            _samples.RemoveRange(_samples.Count - drop, drop);
        }
        StopAndTranscribe();
    }

    private void StopAndTranscribe()
    {
        if (!_recording) return;
        _recording = false;

        var chainEntry = _currentChainEntry;
        _currentChainEntry = null;

        var samples = _samples.ToArray();
        _samples.Clear();

        // Ignore clips shorter than 0.3 s — complete the chain entry immediately so it
        // doesn't block subsequent entries that are waiting behind it.
        if (samples.Length < SampleRate * 3 / 10)
        {
            OutputChain?.Complete(chainEntry!, "");
            SetState(_waveIn is not null ? DictationState.Listening : DictationState.Idle);
            return;
        }

        // Assign sequence number before re-arming so capture order is preserved
        int seq = System.Threading.Interlocked.Increment(ref _captureSeq);

        // Re-arm immediately — mic keeps listening while this chunk transcribes
        SetState(DictationState.Listening);

        // Hand off to pre-warmed worker pool (zero scheduling lag)
        if (_workQueue is not null && !_workQueue.IsAddingCompleted)
            _workQueue.TryAdd((seq, samples, chainEntry));
        else
        {
            // Fallback if pool isn't running (streaming mode or pool not started)
            _pendingResults[seq] = "";
            DrainPendingResults();
            OutputChain?.Complete(chainEntry!, "");
        }

        PendingCountChanged?.Invoke(PendingTranscriptionCount);
    }

    private void DrainPendingResults()
    {
        while (true)
        {
            int next = System.Threading.Volatile.Read(ref _emitSeq) + 1;
            if (!_pendingResults.TryRemove(next, out var text)) break;
            System.Threading.Interlocked.Increment(ref _emitSeq);
            if (!string.IsNullOrWhiteSpace(text))
                TextAvailable?.Invoke(text);
            AnyTranscriptionCompleted?.Invoke();
            PendingCountChanged?.Invoke(PendingTranscriptionCount);
        }
    }

    private void SetState(DictationState s)
    {
        _state = s;
        StateChanged?.Invoke(s);
    }

    // ── Device helpers ─────────────────────────────────────────────────────

    /// <summary>Returns (index, name) for all available WaveIn devices.</summary>
    public static List<(int Index, string Name)> ListInputDevices()
    {
        var list = new List<(int, string)>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            list.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
        return list;
    }

    /// <summary>Maps a saved device name back to its NAudio WaveIn device index (0 = default).</summary>
    public static int FindInputDeviceNumber(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return 0;
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            if (WaveInEvent.GetCapabilities(i).ProductName == deviceName)
                return i;
        return 0;
    }

    // ── Disposal ───────────────────────────────────────────────────────────

    private void DisposeOnlineRecognizer()
    {
        lock (_onlineLock)
        {
            var s = _onlineStream;
            var r = _onlineRecognizer;
            _onlineStream     = null;
            _onlineRecognizer = null;
            s?.Dispose();
            r?.Dispose();
        }
    }

    public void Dispose()
    {
        Deactivate();

        OfflineRecognizer? old;
        lock (_recognizerLock)
        {
            old         = _recognizer;
            _recognizer = null;
        }
        old?.Dispose();

        DisposeOnlineRecognizer();
    }
}
