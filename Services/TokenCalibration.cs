namespace ClaudetRelay.Services;

/// <summary>
/// Tracks a per-participant exponential moving average of chars-per-token,
/// calibrated from real API usage data after each completed response.
/// Thread-safe via lock.
/// </summary>
public sealed class TokenCalibration
{
    private const double Alpha   = 0.2;   // EMA weight for new samples
    private const double Default = 4.0;   // conservative default before any data

    private readonly Dictionary<string, double> _ratios = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Records a calibration sample.
    /// charsOut = total chars sent to the model (history + system prompt content).
    /// tokens   = input tokens reported by the provider for that request.
    /// </summary>
    public void Record(string participantName, int charsOut, int tokens)
    {
        if (tokens <= 0 || charsOut <= 0) return;
        double sample = (double)charsOut / tokens;
        lock (_lock)
        {
            if (_ratios.TryGetValue(participantName, out var prev))
                _ratios[participantName] = (1 - Alpha) * prev + Alpha * sample;
            else
                _ratios[participantName] = sample;
        }
    }

    /// <summary>Returns the calibrated chars-per-token ratio, or 4.0 if no data yet.</summary>
    public double GetCharsPerToken(string participantName)
    {
        lock (_lock)
            return _ratios.TryGetValue(participantName, out var r) ? r : Default;
    }

    /// <summary>Estimates the token count for a given text using the calibrated ratio.</summary>
    public int EstimateTokens(string participantName, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, (int)Math.Ceiling(text.Length / GetCharsPerToken(participantName)));
    }
}
