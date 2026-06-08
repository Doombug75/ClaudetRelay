using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.Wave;
using SherpaOnnx;

namespace ClaudetRelay.Services;

public enum DictationActivationMode { AlwaysOn, PushToTalk, VoiceActivated }
public enum DictationState         { Idle, Listening, Recording, Processing }

/// <summary>
/// Records microphone audio via NAudio and transcribes it using a
/// locally-loaded SherpaOnnx offline ASR model (Whisper or SenseVoice).
///
/// Three activation modes:
///   AlwaysOn       — recording immediately starts when Activate() is called.
///   PushToTalk     — call PttDown()/PttUp() to control recording.
///   VoiceActivated — recording starts automatically when RMS exceeds the
///                    configured threshold, and stops after a period of silence.
/// </summary>
public sealed class DictationService : IDisposable
{
    // ── Events ─────────────────────────────────────────────────────────────
    /// <summary>Fired ~10 times/second with current microphone RMS level (0–1).</summary>
    public event Action<float>?         LevelChanged;
    /// <summary>Fired when transcription is complete with the recognised text.</summary>
    public event Action<string>?        TextAvailable;
    /// <summary>Fired whenever the service state changes.</summary>
    public event Action<DictationState>? StateChanged;

    // ── Config ─────────────────────────────────────────────────────────────
    private DictationActivationMode _mode      = DictationActivationMode.AlwaysOn;
    private float                   _threshold = 0.04f;
    private int                     _deviceNumber = 0;

    // ── Runtime state ──────────────────────────────────────────────────────

    /// <summary>
    /// Microphone gain multiplier. 1.0 = no boost, 2.0 = double, 3.0 = triple.
    /// Applied to raw samples before RMS calculation and before passing to the recognizer.
    /// </summary>
    public float MicBoost { get; set; } = 1.0f;

    private DictationState _state    = DictationState.Idle;
    private WaveInEvent?   _waveIn;
    private List<float>    _samples  = new();
    private bool           _recording       = false;
    private bool           _voiceTriggered  = false;
    private int            _silenceSamples  = 0;
    private OfflineRecognizer? _recognizer;

    private const int SampleRate      = 16000;
    private const int SilenceMs       = 3000;   // silence → stop in VoiceActivated mode
    private const float SilenceFactor = 0.5f;   // silence threshold = threshold * factor

    public bool           IsActive    => _state != DictationState.Idle || _recording;
    public bool           IsRecording => _recording;
    public DictationState State       => _state;

    // ── Model loading ──────────────────────────────────────────────────────

    /// <summary>
    /// Loads an ASR model from <paramref name="modelFolder"/>.
    /// <paramref name="modelType"/> is "whisper" or "sense_voice".
    /// Returns true on success.
    /// </summary>
    public bool LoadModel(string modelType, string modelFolder)
    {
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;

            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = SampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Debug     = 0;
            config.ModelConfig.NumThreads = 2;

            switch (modelType.ToLowerInvariant())
            {
                case "whisper":
                {
                    // Sherpa-onnx Whisper layout:
                    // modelFolder/tiny-encoder.int8.onnx (or small- etc.)
                    // modelFolder/tiny-decoder.int8.onnx
                    // modelFolder/tiny-tokens.txt
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

            _recognizer = new OfflineRecognizer(config);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DictationService.LoadModel error: {ex.Message}");
            return false;
        }
    }

    private static string? FindFile(string folder, string pattern)
    {
        var matches = System.IO.Directory.GetFiles(folder, pattern);
        return matches.Length > 0 ? matches[0] : null;
    }

    // ── Activation ─────────────────────────────────────────────────────────

    public void Configure(DictationActivationMode mode, float threshold, int deviceNumber)
    {
        _mode         = mode;
        _threshold    = Math.Max(0.001f, threshold);
        _deviceNumber = deviceNumber;
    }

    /// <summary>
    /// Start the microphone capture and enter the configured mode.
    /// Pass <paramref name="startRecordingChunk"/> = false to open the mic for level
    /// monitoring / standby without immediately starting to accumulate samples —
    /// useful for pre-loading so the first button press is instant.
    /// </summary>
    public void Activate(bool startRecordingChunk = true)
    {
        if (_waveIn is not null) return;   // already active

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
        BeginRecording();
        SetState(DictationState.Recording);
    }

    /// <summary>Stop capture and release the microphone.</summary>
    public void Deactivate()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        _recording      = false;
        _voiceTriggered = false;
        _samples.Clear();
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
        if (_recording) StopAndTranscribe();
    }

    // ── Internal audio processing ──────────────────────────────────────────

    private void BeginRecording()
    {
        _samples.Clear();
        _silenceSamples = 0;
        _recording      = true;
        SetState(DictationState.Recording);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var boost = MathF.Max(0f, MicBoost);

        // Compute RMS of buffer (boost applied)
        int   count = e.BytesRecorded / 2;
        float sumSq = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            float f = MemoryMarshal.Read<short>(e.Buffer.AsSpan(i)) / 32768f * boost;
            sumSq += f * f;
        }
        float rms = count > 0 ? MathF.Sqrt(sumSq / count) : 0f;
        LevelChanged?.Invoke(rms);

        // Voice-activated trigger
        if (_mode == DictationActivationMode.VoiceActivated && _waveIn is not null)
        {
            if (!_voiceTriggered && rms >= _threshold)
            {
                _voiceTriggered = true;
                BeginRecording();
            }
        }

        if (!_recording) return;

        // Accumulate samples as normalised floats (boosted, clamped to -1..1)
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            _samples.Add(Math.Clamp(MemoryMarshal.Read<short>(e.Buffer.AsSpan(i)) / 32768f * boost, -1f, 1f));
        }

        // Voice-activated: stop after silence
        if (_mode == DictationActivationMode.VoiceActivated)
        {
            if (rms < _threshold * SilenceFactor)
            {
                _silenceSamples += count;
                if (_silenceSamples >= SampleRate * SilenceMs / 1000)
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
    /// Stops the current recording chunk and sends it to the ASR engine.
    /// Use this when the mic button is clicked in AlwaysOn mode so the
    /// accumulated audio gets transcribed before the service is deactivated.
    /// Safe to call from any thread.
    /// </summary>
    public void FinalizeRecording() => StopAndTranscribe();

    private void StopAndTranscribe()
    {
        if (!_recording) return;
        _recording = false;

        var samples = _samples.ToArray();
        _samples.Clear();

        // Ignore clips shorter than 0.3 s
        if (samples.Length < SampleRate * 3 / 10) return;

        SetState(DictationState.Processing);

        Task.Run(() =>
        {
            try
            {
                if (_recognizer is null) return;
                var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(SampleRate, samples);
                _recognizer.Decode(stream);
                var text = stream.Result.Text.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    TextAvailable?.Invoke(text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DictationService transcribe error: {ex.Message}");
            }
            finally
            {
                // In AlwaysOn mode restart recording immediately
                if (_waveIn is not null && _mode == DictationActivationMode.AlwaysOn)
                    BeginRecording();  // sets state to Recording
                else
                    SetState(_waveIn is not null ? DictationState.Listening : DictationState.Idle);
            }
        });
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

    public void Dispose()
    {
        Deactivate();
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
