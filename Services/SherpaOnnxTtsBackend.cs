using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace ClaudetRelay.Services;

/// <summary>
/// TTS backend using Sherpa-onnx (in-process, fully offline).
/// Scans a user-configured folder for VITS/Piper ONNX model subdirectories.
///
/// Expected model folder layout:
///   {ModelFolder}/
///     de_DE-thorsten-high/
///       model.onnx         (or {name}.onnx)
///       tokens.txt
///       [model.onnx.json]  optional — used to detect multi-speaker count
///     en_US-ryan-high/
///       model.onnx
///       tokens.txt
///
/// Multi-speaker models expose one VoiceEntry per speaker:
///   "de_DE-thorsten [Speaker 0]", "de_DE-thorsten [Speaker 1]", ...
/// </summary>
public sealed class SherpaOnnxTtsBackend : ITtsBackend
{
    public string Name => "Sherpa-onnx";

    private readonly string _modelFolder;

    // Cache: modelName → OfflineTts (avoid re-loading model per utterance)
    private readonly Dictionary<string, OfflineTts> _cache = new();
    private readonly object _cacheLock = new();

    public SherpaOnnxTtsBackend(string modelFolder)
    {
        _modelFolder = modelFolder;
    }

    public IReadOnlyList<VoiceEntry> GetVoices()
    {
        if (!Directory.Exists(_modelFolder)) return [];

        var voices = new List<VoiceEntry>();
        foreach (var dir in Directory.GetDirectories(_modelFolder).OrderBy(d => d))
        {
            var name   = Path.GetFileName(dir);
            var onnx   = FindOnnxFile(dir);
            var tokens = FindTokensFile(dir);
            if (onnx is null || tokens is null) continue;

            var numSpeakers = GetNumSpeakers(dir);
            var lang        = name.Length >= 2 ? name[..2].ToUpperInvariant() : "";

            if (numSpeakers <= 1)
            {
                voices.Add(new VoiceEntry(name, lang));
            }
            else
            {
                for (int i = 0; i < numSpeakers; i++)
                    voices.Add(new VoiceEntry($"{name} [Speaker {i}]", lang, i));
            }
        }
        return voices;
    }

    public async Task<byte[]> SynthesizeToWavAsync(
        string text,
        string voiceName,
        float  speed = 1.0f,
        CancellationToken ct = default)
    {
        // Parse "modelName [Speaker N]" or plain "modelName"
        int    speakerId  = 0;
        string modelName  = voiceName;
        var    spkrMatch  = Regex.Match(voiceName, @"^(.*)\s+\[Speaker\s+(\d+)\]$");
        if (spkrMatch.Success)
        {
            modelName = spkrMatch.Groups[1].Value.Trim();
            speakerId = int.Parse(spkrMatch.Groups[2].Value);
        }

        var dir    = Path.Combine(_modelFolder, modelName);
        var onnx   = FindOnnxFile(dir);
        var tokens = FindTokensFile(dir);
        if (onnx is null || tokens is null) return [];

        return await Task.Run(() =>
        {
            // Hold _cacheLock for the full Generate() call so Dispose() cannot free
            // the native OfflineTts object while synthesis is running (ExecutionEngineException).
            lock (_cacheLock)
            {
                OfflineTts tts = GetOrCreateTts(modelName, onnx, tokens);
                var audio = tts.Generate(text, speed, speakerId);
                return PcmToWav(audio.Samples, audio.SampleRate);
            }
        }, ct);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(Directory.Exists(_modelFolder) && GetVoices().Count > 0);

    public void Dispose()
    {
        lock (_cacheLock)
        {
            foreach (var tts in _cache.Values) try { tts.Dispose(); } catch { }
            _cache.Clear();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private OfflineTts GetOrCreateTts(string modelName, string onnxPath, string tokensPath)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(modelName, out var cached)) return cached;

            var config = new OfflineTtsConfig
            {
                Model = new OfflineTtsModelConfig
                {
                    Vits = new OfflineTtsVitsModelConfig
                    {
                        Model  = onnxPath,
                        Tokens = tokensPath,
                    },
                    NumThreads = 2,
                    Provider   = "cpu",
                    Debug      = 0,
                },
                MaxNumSentences = 1,
            };

            var tts = new OfflineTts(config);
            _cache[modelName] = tts;
            return tts;
        }
    }

    private static string? FindOnnxFile(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "*.onnx")
            .FirstOrDefault(f => !f.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindTokensFile(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "tokens.txt").FirstOrDefault()
            : null;

    private static int GetNumSpeakers(string dir)
    {
        var json = Directory.GetFiles(dir, "*.onnx.json").FirstOrDefault();
        if (json is null) return 1;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (doc.RootElement.TryGetProperty("num_speakers", out var ns))
                return Math.Max(1, ns.GetInt32());
        }
        catch { }
        return 1;
    }

    private static byte[] PcmToWav(float[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int dataSize = samples.Length * 2;   // 16-bit mono

        // RIFF WAV header
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);            // chunk size
        bw.Write((short)1);      // PCM
        bw.Write((short)1);      // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2); // byte rate
        bw.Write((short)2);      // block align
        bw.Write((short)16);     // bits per sample
        bw.Write("data"u8.ToArray());
        bw.Write(dataSize);

        foreach (var s in samples)
            bw.Write((short)Math.Clamp((int)(s * 32767f), short.MinValue, short.MaxValue));

        return ms.ToArray();
    }
}
