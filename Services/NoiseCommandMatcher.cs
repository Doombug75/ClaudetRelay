using System;
using System.Collections.Generic;
using System.Linq;
using ClaudetRelay.Models;

namespace ClaudetRelay.Services;

/// <summary>
/// Matches incoming audio transients against stored noise-command templates.
///
/// Strategy: resample every captured clip to a fixed number of points, normalise
/// amplitude, and compare the RAW WAVEFORM SHAPE via normalised cross-correlation.
/// This avoids collapsing short transients (e.g. a 30 ms finger snap) into a handful
/// of frequency-band averages, which destroys the discriminating fine structure.
///
/// Detection flow:
///   1. Monitor RMS against a sliding noise floor; start capturing on onset.
///   2. Stop capturing after 250 ms of silence (or 2.5 s hard cap = discard as speech).
///   3. Resample the clip to WaveformPoints, amplitude-normalise, keep ZCR + duration.
///   4. Compare against all stored reference waveforms; fire best match if within threshold.
/// </summary>
public sealed class NoiseCommandMatcher
{
    public event Action<VoiceCommand>? CommandFired;

    /// <summary>Set this to automatically timestamp-sort noise commands into the output chain.</summary>
    public OutputChain? OutputChain { get; set; }

    /// <summary>Diagnostic info about the last processed clip — set even on rejections.</summary>
    public string LastClipInfo { get; private set; } = "(no clip yet)";

    /// <summary>Sample count of the last successfully matched clip (at 16 kHz). Used by the caller to trim the noise from the ASR buffer.</summary>
    public int LastMatchedClipSamples { get; private set; }

    private const int SampleRate = 16000;

    // Number of resampled points per clip.
    // Large enough to preserve fine structure of a 30 ms snap (480 samples → 1024 points,
    // i.e. interpolated but not lost), yet dense enough for a 1 s fart (~16x compression).
    private const int WaveformPoints = 1024;

    // Maximum shift tried during cross-correlation alignment (fraction of WaveformPoints).
    // ±10 % = ±102 samples: tolerates onset-detection jitter and slight timing variation
    // in how quickly a sound reaches peak amplitude.
    private const int MaxShiftFraction = 10; // shift = WaveformPoints / MaxShiftFraction

    // Number of RMS windows for the energy envelope (captures ADSR shape).
    private const int EnvelopeBins = 16;

    // Number of log-spaced frequency bands for the spectral fingerprint.
    // This is the PRIMARY discriminator for short impulsive transients: a finger snap
    // is broadband (energy spread to high frequencies), a tongue click resonates lower
    // (~0.5-2 kHz). Raw waveform shape is unreliable for clicks (varies shot-to-shot),
    // but the band-energy distribution is repeatable.
    private const int SpectralBands = 12;

    private const int MaxCaptureSamples        = SampleRate * 4;           // 4 s hard cap
    private const int SilenceEndSamples        = (int)(SampleRate * 0.25); // 250 ms silence ends clip
    private const int MaxNoiseSamples          = (int)(SampleRate * 2.5);  // >2.5 s = speech, discard
    private const int CooldownSamples          = SampleRate * 2;           // 2 s lockout after match
    private const int AsrOutputSuppressSamples = (int)(SampleRate * 0.35); // 350 ms lockout after ASR

    private float            _noiseFloor        = 0.01f;
    private bool             _capturing         = false;
    private readonly List<float> _capture       = new();
    private int              _silenceSamples    = 0;
    private int              _cooldownRemaining = 0;
    private List<VoiceCommand> _commands        = new();

    // ── Public API ─────────────────────────────────────────────────────────

    public void NotifyAsrOutput()
    {
        _cooldownRemaining = Math.Max(_cooldownRemaining, AsrOutputSuppressSamples);
        _capture.Clear();
        _capturing      = false;
        _silenceSamples = 0;
    }

    public void UpdateCommands(IEnumerable<VoiceCommand> commands)
    {
        _commands = commands
            .Where(c => c.Enabled
                     && c.Type != VoiceCommandType.Phrase
                     && c.NoiseSamples.Any(s => s is not null))
            .ToList();
    }

    public void ProcessSamples(float[] samples)
    {
        if (_commands.Count == 0)
        {
            LastClipInfo = "(no commands loaded)";
            return;
        }

        if (_cooldownRemaining > 0)
        {
            _cooldownRemaining = Math.Max(0, _cooldownRemaining - samples.Length);
            if (!_capturing)
                _noiseFloor = _noiseFloor * 0.995f + Rms(samples) * 0.005f;
            return;
        }

        float rms = Rms(samples);

        if (!_capturing)
        {
            _noiseFloor = _noiseFloor * 0.995f + rms * 0.005f;
            if (rms >= MathF.Max(_noiseFloor * 4f, 0.025f))
            {
                _capturing = true;
                _capture.Clear();
                _silenceSamples = 0;
            }
        }

        if (_capturing)
        {
            _capture.AddRange(samples);

            float silenceThresh = MathF.Max(_noiseFloor * 2.5f, 0.012f);
            _silenceSamples = rms < silenceThresh ? _silenceSamples + samples.Length : 0;

            if (_capture.Count >= MaxNoiseSamples || _capture.Count >= MaxCaptureSamples)
            {
                LastClipInfo = $"DISCARD  too long ({_capture.Count / (float)SampleRate:F2}s)";
                _capture.Clear(); _capturing = false; _silenceSamples = 0;
            }
            else if (_silenceSamples >= SilenceEndSamples)
            {
                var clip = _capture.ToArray();
                _capture.Clear(); _capturing = false; _silenceSamples = 0;
                TryMatch(clip);
            }
        }
    }

    // ── Matching ───────────────────────────────────────────────────────────

    private void TryMatch(float[] clip)
    {
        if (clip.Length < SampleRate / 25)
        {
            LastClipInfo = $"SKIP  too short ({clip.Length} samples)";
            return;
        }

        var features = ExtractFeatures(clip);
        var scored   = new List<(VoiceCommand cmd, float dist, float threshold)>();

        foreach (var cmd in _commands)
        {
            var refs = cmd.NoiseSamples
                .Where(s => s is not null)
                .Select(s => ExtractFeatures(s!))
                .ToList();
            if (refs.Count == 0) continue;

            // Centroid = mean of the reference waveforms.
            // Spread   = how far the most outlying reference sits from the centroid.
            // Threshold = spread × tolerance factor — anything closer to the centroid
            //             than the furthest reference (× factor) is "inside the window".
            var centroid  = Centroid(refs);
            float spread  = refs.Max(r => Distance(r, centroid));
            float threshold = MathF.Max(spread * 1.8f, 0.12f);  // never below 0.12

            float dist = Distance(centroid, features);
            scored.Add((cmd, dist, threshold));
        }

        if (scored.Count == 0) { LastClipInfo = "SKIP  no scoreable commands"; return; }
        scored.Sort((a, b) => a.dist.CompareTo(b.dist));

        var (best, bestDist, bestThreshold) = scored[0];
        string allScores = string.Join(" | ", scored.Select(s => $"{s.cmd.Name}={s.dist:F3}/{s.threshold:F3}"));

        if (bestDist >= bestThreshold)
        {
            LastClipInfo = $"REJECT  dist={bestDist:F3} >= thresh={bestThreshold:F3}  [{allScores}]";
            return;
        }

        _cooldownRemaining     = CooldownSamples;
        LastMatchedClipSamples = clip.Length;
        LastClipInfo = $"MATCH  {best.Name}  dist={bestDist:F3}  thresh={bestThreshold:F3}  [{allScores}]";
        // Add to chain before firing CommandFired — still on the audio thread, so the
        // timestamp is accurate and the entry sorts after any speech recording that
        // started earlier.
        OutputChain?.AddCommand(DateTime.UtcNow, best);
        CommandFired?.Invoke(best);
    }

    /// <summary>
    /// Returns the mean feature vector of the given references.
    /// The centroid waveform is the point-wise average of all reference waveforms;
    /// ZCR and duration are also averaged.
    /// </summary>
    private static NoiseFeatures Centroid(List<NoiseFeatures> refs)
    {
        var wave  = new float[WaveformPoints];
        var env   = new float[EnvelopeBins];
        var bands = new float[SpectralBands];
        float zcr = 0f, dur = 0f, cent = 0f;
        foreach (var r in refs)
        {
            for (int i = 0; i < WaveformPoints; i++) wave[i]  += r.Waveform[i];
            for (int i = 0; i < EnvelopeBins; i++)   env[i]   += r.EnergyEnvelope[i];
            for (int i = 0; i < SpectralBands; i++)  bands[i] += r.SpectralBands[i];
            zcr  += r.Zcr;
            dur  += r.Duration;
            cent += r.SpectralCentroid;
        }
        float n = refs.Count;
        for (int i = 0; i < WaveformPoints; i++) wave[i]  /= n;
        for (int i = 0; i < EnvelopeBins; i++)   env[i]   /= n;
        for (int i = 0; i < SpectralBands; i++)  bands[i] /= n;
        return new NoiseFeatures(wave, zcr / n, dur / n, env, cent / n, bands);
    }

    // ── Feature extraction ─────────────────────────────────────────────────

    /// <summary>
    /// Trims silence from both ends of the clip, then resamples the active sound
    /// portion to WaveformPoints via linear interpolation, and amplitude-normalises
    /// to max absolute value = 1.
    ///
    /// Trimming is critical: a 30 ms snap followed by 220 ms of trailing silence
    /// would otherwise fill only 12 % of the resampled vector; the remaining 88 %
    /// of silence — once normalised — becomes amplified noise that looks identical
    /// across all sounds and destroys cross-correlation discrimination.
    /// </summary>
    public static NoiseFeatures ExtractFeatures(float[] samples)
    {
        // ── 1. Find peak amplitude ─────────────────────────────────────────
        float peak = 0f;
        foreach (float v in samples) peak = MathF.Max(peak, MathF.Abs(v));
        float silThresh = MathF.Max(peak * 0.04f, 1e-4f);  // 4 % of peak = silence

        // ── 2. Trim leading silence ────────────────────────────────────────
        int trimStart = 0;
        while (trimStart < samples.Length - 1 && MathF.Abs(samples[trimStart]) < silThresh)
            trimStart++;
        trimStart = Math.Max(0, trimStart - (int)(SampleRate * 0.005)); // keep 5 ms pre-attack

        // ── 3. Trim trailing silence ───────────────────────────────────────
        int trimEnd = samples.Length - 1;
        while (trimEnd > trimStart && MathF.Abs(samples[trimEnd]) < silThresh)
            trimEnd--;
        trimEnd = Math.Min(samples.Length - 1, trimEnd + (int)(SampleRate * 0.02)); // keep 20 ms tail

        int len = trimEnd - trimStart + 1;
        if (len < 16) { trimStart = 0; len = samples.Length; } // fallback: nothing to trim

        // ── 4. Resample active portion to WaveformPoints ───────────────────
        var wave = new float[WaveformPoints];
        double ratio = (double)(len - 1) / (WaveformPoints - 1);
        for (int i = 0; i < WaveformPoints; i++)
        {
            double pos  = i * ratio;
            int    lo   = trimStart + (int)pos;
            int    hi   = Math.Min(lo + 1, trimStart + len - 1);
            float  frac = (float)(pos - (int)pos);
            wave[i] = samples[lo] * (1f - frac) + samples[hi] * frac;
        }

        // ── 5. Amplitude normalise ─────────────────────────────────────────
        float maxAbs = 0f;
        foreach (float v in wave) maxAbs = MathF.Max(maxAbs, MathF.Abs(v));
        if (maxAbs > 1e-6f)
            for (int i = 0; i < WaveformPoints; i++) wave[i] /= maxAbs;

        // ── 6. ZCR on the trimmed active portion ───────────────────────────
        int crossings = 0;
        for (int i = trimStart + 1; i <= trimEnd; i++)
            if ((samples[i] >= 0f) != (samples[i - 1] >= 0f)) crossings++;
        float zcr = (float)crossings / len;

        float duration = (float)len / SampleRate;  // trimmed duration

        // ── 7. Energy envelope: 16 RMS windows over the trimmed clip ──────
        // Captures the ADSR shape — a snap has a sharp spike then silence,
        // a tongue click has a slightly slower resonant decay, a fart sustains.
        var envelope = new float[EnvelopeBins];
        int binLen = Math.Max(1, len / EnvelopeBins);
        for (int b = 0; b < EnvelopeBins; b++)
        {
            int s2 = trimStart + b * binLen;
            int e2 = Math.Min(s2 + binLen, trimStart + len);
            float sum = 0f;
            for (int i = s2; i < e2; i++) sum += samples[i] * samples[i];
            envelope[b] = e2 > s2 ? MathF.Sqrt(sum / (e2 - s2)) : 0f;
        }
        float envMax = 0f;
        foreach (float v in envelope) envMax = MathF.Max(envMax, v);
        if (envMax > 1e-6f)
            for (int i = 0; i < EnvelopeBins; i++) envelope[i] /= envMax;

        // ── 8. Spectral fingerprint via FFT ────────────────────────────────
        // Band-energy distribution is the primary discriminator for impulsive
        // transients: snap = broadband (energy reaches high bands), tongue click =
        // oral-cavity resonance concentrated in low-mid bands. Centroid is derived
        // from the same FFT as a cheap summary scalar.
        ComputeSpectralFeatures(samples, trimStart, len, out var bands, out float centroid);

        return new NoiseFeatures(wave, zcr, duration, envelope, centroid, bands);
    }

    // ── Distance ───────────────────────────────────────────────────────────

    /// <summary>
    /// Computes normalised cross-correlation between two waveform shapes,
    /// trying small time shifts to tolerate onset-detection jitter.
    /// Returns a distance in [0, 1]: 0 = identical, 1 = maximally dissimilar.
    /// </summary>
    private static float Distance(NoiseFeatures a, NoiseFeatures b)
    {
        // Pre-compute self-energies (used to normalise each shift's dot product)
        float na = 0f, nb = 0f;
        for (int i = 0; i < WaveformPoints; i++)
        {
            na += a.Waveform[i] * a.Waveform[i];
            nb += b.Waveform[i] * b.Waveform[i];
        }
        float norm = MathF.Sqrt(na * nb);
        if (norm < 1e-8f) return 0.5f; // both silent — treat as neutral

        // Try shifts in [-maxShift, +maxShift]; keep the best (highest) dot product
        int   maxShift = WaveformPoints / MaxShiftFraction;
        float bestDot  = float.MinValue;

        for (int shift = -maxShift; shift <= maxShift; shift++)
        {
            int start = Math.Max(0, shift);
            int end   = Math.Min(WaveformPoints, WaveformPoints + shift);
            float dot = 0f;
            for (int i = start; i < end; i++)
                dot += a.Waveform[i] * b.Waveform[i - shift];
            if (dot > bestDot) bestDot = dot;
        }

        // corr ∈ [-1, 1].  Convert to distance ∈ [0, 1]:  0 = perfect match.
        float corr     = bestDot / norm;
        float waveDist = (1f - corr) * 0.5f;

        // Energy envelope: RMS difference between ADSR shapes (captures attack/decay profile)
        float envDist = 0f;
        for (int i = 0; i < EnvelopeBins; i++)
        {
            float d = a.EnergyEnvelope[i] - b.EnergyEnvelope[i];
            envDist += d * d;
        }
        envDist = MathF.Min(MathF.Sqrt(envDist / EnvelopeBins) * 2f, 1f);

        // Spectral band fingerprint: the PRIMARY discriminator for impulsive transients.
        // RMS difference between the log-energy band vectors — captures *where* the
        // energy sits in frequency, which is what separates a snap (broadband) from a
        // tongue click (low-mid resonance), and is far more repeatable than waveform shape.
        float bandDist = 0f;
        for (int i = 0; i < SpectralBands; i++)
        {
            float d = a.SpectralBands[i] - b.SpectralBands[i];
            bandDist += d * d;
        }
        bandDist = MathF.Min(MathF.Sqrt(bandDist / SpectralBands) * 1.5f, 1f);

        // Spectral centroid: brightness difference (snap=high broadband, click=low resonance)
        float centDist = MathF.Min(MathF.Abs(a.SpectralCentroid - b.SpectralCentroid) * 3f, 1f);

        // ZCR adds a cheap secondary discriminator for voiced vs noisy sounds
        float zcrDist = MathF.Min(MathF.Abs(a.Zcr - b.Zcr) * 8f, 1f);

        // Log-duration: helps separate snap (short) from fart (long) when waveforms are ambiguous
        float durDist = MathF.Min(MathF.Abs(
            MathF.Log(a.Duration + 0.01f) - MathF.Log(b.Duration + 0.01f)), 1f);

        // Spectral content now drives discrimination (bands + centroid = 0.44 combined),
        // with waveform shape demoted to a secondary cue. This is what lets short clicks
        // and snaps separate reliably — their spectra differ even when waveforms don't.
        return bandDist * 0.38f
             + waveDist * 0.34f
             + centDist * 0.06f
             + envDist  * 0.10f
             + zcrDist  * 0.08f
             + durDist  * 0.04f;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static float Rms(ReadOnlySpan<float> s)
    {
        if (s.Length == 0) return 0f;
        float sum = 0f;
        foreach (float v in s) sum += v * v;
        return MathF.Sqrt(sum / s.Length);
    }

    private static float Rms(float[] s) => Rms(s.AsSpan());

    // ── FFT helpers ────────────────────────────────────────────────────────

    /// <summary>In-place Cooley-Tukey FFT. Array lengths must be a power of two.</summary>
    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = -2f * MathF.PI / len;
            float wr  = MathF.Cos(ang), wi = MathF.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1f, ci = 0f;
                for (int j = 0; j < len / 2; j++)
                {
                    float ur = re[i + j],         ui = im[i + j];
                    float vr = re[i + j + len/2] * cr - im[i + j + len/2] * ci;
                    float vi = re[i + j + len/2] * ci + im[i + j + len/2] * cr;
                    re[i + j]         = ur + vr;  im[i + j]         = ui + vi;
                    re[i + j + len/2] = ur - vr;  im[i + j + len/2] = ui - vi;
                    float newCr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;  cr = newCr;
                }
            }
        }
    }

    /// <summary>
    /// Computes both the log-spaced band-energy fingerprint and the spectral centroid
    /// of the given slice from a single FFT.
    ///
    /// <paramref name="bands"/>: <see cref="SpectralBands"/> values. Each is the log
    ///   energy in a log-spaced frequency band, then the whole vector is mean-removed
    ///   and scaled to unit max so it describes the *shape* of the spectrum
    ///   (where energy sits) independent of overall loudness.
    /// <paramref name="centroid"/>: frequency centroid in [0, 1] (0 = DC, 1 = Nyquist).
    /// </summary>
    private static void ComputeSpectralFeatures(float[] samples, int start, int len,
                                                out float[] bands, out float centroid)
    {
        bands    = new float[SpectralBands];
        centroid = 0.5f;

        int fftSize = 1;
        while (fftSize < Math.Min(len, 4096)) fftSize <<= 1;
        if (fftSize < 2) return;

        var re = new float[fftSize];
        var im = new float[fftSize];
        int copyLen = Math.Min(len, fftSize);
        for (int i = 0; i < copyLen; i++)
        {
            float window = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / Math.Max(copyLen - 1, 1)));
            re[i] = samples[start + i] * window;
        }

        Fft(re, im);

        int half = fftSize / 2;
        if (half < 2) return;

        // ── Spectral centroid ──────────────────────────────────────────────
        float weightedSum = 0f, totalMag = 0f;
        for (int i = 1; i < half; i++)   // skip DC bin
        {
            float mag = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
            weightedSum += i * mag;
            totalMag    += mag;
        }
        if (totalMag > 1e-8f) centroid = weightedSum / (totalMag * half);

        // ── Log-spaced band energies ───────────────────────────────────────
        // Band edges grow geometrically across the [1, half) bin range so low-mid
        // frequencies (where a tongue click resonates) get finer resolution than the
        // top octave — matching how the ear and these sounds are organised.
        float logLo = MathF.Log(1f);
        float logHi = MathF.Log(half);
        for (int b = 0; b < SpectralBands; b++)
        {
            int binLo = (int)MathF.Exp(logLo + (logHi - logLo) * b       / SpectralBands);
            int binHi = (int)MathF.Exp(logLo + (logHi - logLo) * (b + 1) / SpectralBands);
            binLo = Math.Clamp(binLo, 1, half - 1);
            binHi = Math.Clamp(Math.Max(binHi, binLo + 1), 2, half);

            float energy = 0f;
            for (int i = binLo; i < binHi; i++)
                energy += re[i] * re[i] + im[i] * im[i];
            energy /= (binHi - binLo);
            bands[b] = MathF.Log(energy + 1e-8f);   // log scale = perceptual + compresses dynamic range
        }

        // Normalise the band vector to describe spectral SHAPE, not loudness:
        // subtract the mean, then scale so the largest deviation is 1.
        float mean = 0f;
        for (int b = 0; b < SpectralBands; b++) mean += bands[b];
        mean /= SpectralBands;
        float maxDev = 0f;
        for (int b = 0; b < SpectralBands; b++)
        {
            bands[b] -= mean;
            maxDev = MathF.Max(maxDev, MathF.Abs(bands[b]));
        }
        if (maxDev > 1e-6f)
            for (int b = 0; b < SpectralBands; b++) bands[b] /= maxDev;
    }
}

/// <summary>Feature vector for a noise clip.</summary>
public sealed record NoiseFeatures(
    float[] Waveform,        // WaveformPoints samples, resampled + amplitude-normalised to max=1
    float   Zcr,             // zero-crossing rate on the trimmed signal
    float   Duration,        // trimmed clip length in seconds
    float[] EnergyEnvelope,  // EnvelopeBins RMS windows, normalised to max=1 (ADSR shape)
    float   SpectralCentroid, // frequency centroid normalised to [0,1] (0=DC, 1=Nyquist)
    float[] SpectralBands);  // SpectralBands log-spaced band energies (log scale, normalised)
