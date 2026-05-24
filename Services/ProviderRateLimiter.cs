namespace ClaudetRelay.Services;

/// <summary>
/// Serialises cloud API requests so they do not exceed a given requests-per-minute
/// budget. Thread-safe: concurrent callers queue through a SemaphoreSlim so each
/// waits for the minimum inter-request interval before proceeding.
///
/// Usage:
///   await rateLimiter.WaitAsync(ct);   // may delay; then returns
///   // … send the actual HTTP request …
/// </summary>
public sealed class ProviderRateLimiter
{
    private readonly SemaphoreSlim _sem  = new(1, 1);
    private          DateTime      _last = DateTime.MinValue;
    private volatile int           _rpm;

    /// <summary>Current requests-per-minute limit (always ≥ 1).</summary>
    public int Rpm => _rpm;

    public ProviderRateLimiter(int rpm) => _rpm = Math.Max(1, rpm);

    /// <summary>Atomically update the RPM limit (takes effect on the next call).</summary>
    public void UpdateRpm(int rpm) => _rpm = Math.Max(1, rpm);

    /// <summary>
    /// Blocks (asynchronously) until at least <c>60 000 / Rpm</c> ms have elapsed
    /// since the previous call, then stamps the current time and returns.
    /// Cancellation is propagated to the awaiter.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var intervalMs = 60_000.0 / _rpm;
            var elapsedMs  = (DateTime.UtcNow - _last).TotalMilliseconds;
            var waitMs     = intervalMs - elapsedMs;
            if (waitMs > 0)
                await Task.Delay((int)Math.Ceiling(waitMs), ct).ConfigureAwait(false);
            _last = DateTime.UtcNow;
        }
        finally
        {
            _sem.Release();
        }
    }
}
