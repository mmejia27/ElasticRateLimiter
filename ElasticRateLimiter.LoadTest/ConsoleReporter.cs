namespace ElasticRateLimiter.LoadTest;

/// <summary>
/// Repaints a single status line in place while the run is going, and prints one permanent note the
/// moment the limiter first pushes back.
/// </summary>
/// <remarks>
/// In-place repainting needs a real terminal. When stdout is redirected - a pipe, a file, CI - the
/// carriage returns would collapse the whole run onto one unreadable line, so the reporter falls
/// back to writing a line per tick.
/// </remarks>
/// <param name="repaintInPlace">
/// Overrides the terminal detection. Leave null outside of tests.
/// </param>
public sealed class ConsoleReporter(LoadTestOptions options, Metrics metrics, bool? repaintInPlace = null)
{
    private readonly bool _repaintInPlace = repaintInPlace ?? !Console.IsOutputRedirected;

    private long _lastSent;
    private long _lastAllowed;
    private long _lastLimited;
    private TimeSpan _lastAt = TimeSpan.Zero;
    private bool _announcedFirstLimit;
    private int _statusLength;

    public async Task RunAsync(CancellationToken token)
    {
        using var ticker = new PeriodicTimer(options.ReportInterval);
        try
        {
            while (await ticker.WaitForNextTickAsync(token))
                Tick();
        }
        catch (OperationCanceledException)
        {
            // Run finished.
        }
    }

    private void Tick()
    {
        var now = metrics.Elapsed;
        var window = (now - _lastAt).TotalSeconds;
        if (window <= 0) return;

        var sent = metrics.Sent;
        var allowed = metrics.Allowed;
        var limited = metrics.Limited;

        var sentRate = (sent - _lastSent) / window;
        var allowedRate = (allowed - _lastAllowed) / window;
        var limitedRate = (limited - _lastLimited) / window;

        _lastSent = sent; _lastAllowed = allowed; _lastLimited = limited; _lastAt = now;

        AnnounceFirstLimitOnce();

        WriteStatus(
            $"[{now.TotalSeconds,5:F1}s] in-flight {metrics.InFlight,3}/{options.Concurrency,-3} " +
            $"backoff {metrics.Retrying,3} " +
            $"| sent {sentRate,5:F0}/s | ok {allowedRate,5:F0}/s | 429 {limitedRate,5:F0}/s " +
            $"| totals ok={allowed} 429={limited} retries={metrics.Retries} err={metrics.Errors}");
    }

    private void AnnounceFirstLimitOnce()
    {
        if (_announcedFirstLimit || metrics.FirstLimitedAt is not { } at) return;
        _announcedFirstLimit = true;

        WritePermanent(ConsoleColor.Yellow,
            $"  >> RATE LIMITED after {at.TotalSeconds:F1}s and {metrics.Sent} requests");
        WritePermanent(ConsoleColor.DarkYellow,
            $"     {metrics.LastLimitReason}");
        WritePermanent(ConsoleColor.DarkGray,
            "     retrying with the server's Retry-After delay...");
    }

    /// <summary>Repaints the status line, padding over whatever the previous, longer line left behind.</summary>
    private void WriteStatus(string text)
    {
        if (!_repaintInPlace)
        {
            Console.WriteLine(text);
            return;
        }

        // A line wider than the window wraps, and then the carriage return only rewinds to the start
        // of the last visual row - leaving debris above. Truncating keeps the repaint on one row.
        var width = WindowWidth();
        if (width > 0 && text.Length > width - 1)
            text = text[..(width - 1)];

        var padding = Math.Max(0, _statusLength - text.Length);
        Console.Write('\r');
        Console.Write(text);
        if (padding > 0)
        {
            Console.Write(new string(' ', padding));
            Console.Write('\r');
            Console.Write(text);
        }

        _statusLength = text.Length;
    }

    /// <summary>Writes a line that stays in the scrollback, without the status line bleeding into it.</summary>
    private void WritePermanent(ConsoleColor color, string text)
    {
        ClearStatus();
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    private void ClearStatus()
    {
        if (!_repaintInPlace || _statusLength is 0) return;

        Console.Write('\r');
        Console.Write(new string(' ', _statusLength));
        Console.Write('\r');
        _statusLength = 0;
    }

    private static int WindowWidth()
    {
        // Throws on some hosts (no attached console buffer); a width of 0 just disables truncation.
        try { return Console.WindowWidth; }
        catch (IOException) { return 0; }
        catch (PlatformNotSupportedException) { return 0; }
    }

    public void WriteSummary()
    {
        ClearStatus();

        var m = metrics;
        var cost = QueryBodies.CostOf(options.Shape);

        Console.WriteLine();
        WritePermanent(ConsoleColor.Cyan, "=== summary ===");
        Console.WriteLine($"  target            {options.Target}{options.Index}/_search");
        Console.WriteLine($"  shape / priority  {options.Shape} (~{cost} tokens each) / {options.Priority}");
        Console.WriteLine($"  ran for           {m.Elapsed.TotalSeconds:F1}s at up to {options.Concurrency} in flight (peak {m.PeakInFlight})");
        Console.WriteLine();
        Console.WriteLine($"  attempts sent     {m.Sent}");
        Console.WriteLine($"  allowed (2xx)     {m.Allowed}   ({m.AllowedAfterRetry} of them only after a retry)");
        Console.WriteLine($"  rate limited      {m.Limited}");
        Console.WriteLine($"  retries issued    {m.Retries}");
        Console.WriteLine($"  gave up           {m.Exhausted}   (still 429 after {options.MaxRetries} retries)");
        Console.WriteLine($"  transport errors  {m.Errors}{(m.Errors > 0 ? $"   last: {m.LastError}" : string.Empty)}");
        Console.WriteLine($"  mean latency      {m.MeanLatency.TotalMilliseconds:F1} ms");

        Console.WriteLine();
        if (m.Limited is 0)
        {
            WritePermanent(ConsoleColor.Yellow,
                "  The limiter never pushed back - the bucket kept up with this load.");
            Console.WriteLine(
                "  Raise --rps, raise --concurrency, or use a costlier --shape (wildcard/expensive).");
        }
        else
        {
            var firstAt = m.FirstLimitedAt ?? TimeSpan.Zero;
            WritePermanent(ConsoleColor.Green,
                $"  Limiter engaged after {firstAt.TotalSeconds:F1}s; {m.Limited} of {m.Sent} attempts were throttled.");
            if (m.Exhausted is 0 && m.Retries > 0)
                WritePermanent(ConsoleColor.Green,
                    "  Every throttled request eventually succeeded on retry.");
            else if (m.Exhausted > 0)
                WritePermanent(ConsoleColor.Yellow,
                    $"  {m.Exhausted} request(s) never got through - the offered load exceeds the refill rate.");
        }
    }
}
