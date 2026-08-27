using System.Diagnostics;

namespace ElasticRateLimiter.LoadTest;

/// <summary>
/// Counters shared by every worker. All mutation goes through Interlocked so the reporter can read
/// a consistent-enough snapshot without taking a lock on the hot path.
/// </summary>
public sealed class Metrics
{
    private int _inFlight;
    private int _peakInFlight;
    private int _retrying;
    private long _sent;
    private long _allowed;
    private long _allowedAfterRetry;
    private long _limited;
    private long _retries;
    private long _exhausted;
    private long _errors;
    private long _latencyTicks;
    private long _latencySamples;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _firstLimitedAtTicks = -1;
    private string _lastLimitReason = string.Empty;
    private string _lastError = string.Empty;

    public int InFlight => Volatile.Read(ref _inFlight);
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>Requests that were throttled and are sleeping before their next attempt.</summary>
    public int Retrying => Volatile.Read(ref _retrying);
    public long Sent => Interlocked.Read(ref _sent);
    public long Allowed => Interlocked.Read(ref _allowed);
    public long AllowedAfterRetry => Interlocked.Read(ref _allowedAfterRetry);
    public long Limited => Interlocked.Read(ref _limited);
    public long Retries => Interlocked.Read(ref _retries);
    public long Exhausted => Interlocked.Read(ref _exhausted);
    public long Errors => Interlocked.Read(ref _errors);
    public string LastLimitReason => Volatile.Read(ref _lastLimitReason);
    public string LastError => Volatile.Read(ref _lastError);
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>When the limiter first pushed back, or null if it never did.</summary>
    public TimeSpan? FirstLimitedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _firstLimitedAtTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public TimeSpan MeanLatency
    {
        get
        {
            var samples = Interlocked.Read(ref _latencySamples);
            return samples is 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Interlocked.Read(ref _latencyTicks) / samples);
        }
    }

    public void RequestStarted()
    {
        Interlocked.Increment(ref _sent);
        var current = Interlocked.Increment(ref _inFlight);

        // Raise the peak watermark; retry until we win or someone recorded a higher value.
        int observed;
        while (current > (observed = Volatile.Read(ref _peakInFlight)))
        {
            if (Interlocked.CompareExchange(ref _peakInFlight, current, observed) == observed)
                break;
        }
    }

    public void RequestFinished() => Interlocked.Decrement(ref _inFlight);

    public void RecordAllowed(TimeSpan latency, bool afterRetry)
    {
        Interlocked.Increment(ref _allowed);
        if (afterRetry) Interlocked.Increment(ref _allowedAfterRetry);
        Interlocked.Add(ref _latencyTicks, latency.Ticks);
        Interlocked.Increment(ref _latencySamples);
    }

    public void RecordLimited(TimeSpan latency, string reason)
    {
        Interlocked.Increment(ref _limited);
        Interlocked.CompareExchange(ref _firstLimitedAtTicks, _clock.Elapsed.Ticks, -1);
        Volatile.Write(ref _lastLimitReason, reason);
        Interlocked.Add(ref _latencyTicks, latency.Ticks);
        Interlocked.Increment(ref _latencySamples);
    }

    public void RecordRetryScheduled(TimeSpan delay) => Interlocked.Increment(ref _retries);

    /// <summary>Brackets the sleep between attempts so the reporter can show requests parked on backoff.</summary>
    public IDisposable TrackRetryWait()
    {
        Interlocked.Increment(ref _retrying);
        return new RetryWait(this);
    }

    private sealed class RetryWait(Metrics owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._retrying);
    }

    public void RecordExhausted() => Interlocked.Increment(ref _exhausted);

    public void RecordError(string message)
    {
        Interlocked.Increment(ref _errors);
        Volatile.Write(ref _lastError, message);
    }
}
