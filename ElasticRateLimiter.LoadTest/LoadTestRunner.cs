using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ElasticRateLimiter.LoadTest;

public sealed class LoadTestRunner(LoadTestOptions options, HttpClient http, Metrics metrics)
{
    private readonly string _body = QueryBodies.For(options.Shape);
    private readonly string _path = $"/{options.Index}/_search";

    public async Task RunAsync(CancellationToken token)
    {
        // One permit per allowed in-flight request; workers block here rather than queueing
        // unbounded work, so "in flight" stays an accurate reading of real concurrency.
        using var concurrency = new SemaphoreSlim(options.Concurrency, options.Concurrency);
        var pacer = options.RequestsPerSecond > 0
            ? new PeriodicTimer(TimeSpan.FromSeconds(1.0 / options.RequestsPerSecond))
            : null;

        var running = new List<Task>();
        var deadline = Stopwatch.StartNew();

        try
        {
            while (deadline.Elapsed < options.Duration && !token.IsCancellationRequested)
            {
                if (pacer is not null && !await pacer.WaitForNextTickAsync(token))
                    break;

                await concurrency.WaitAsync(token);

                running.Add(Task.Run(async () =>
                {
                    try { await SendWithRetriesAsync(token); }
                    finally { concurrency.Release(); }
                }, token));

                // Keep the tracking list from growing without bound over a long run.
                if (running.Count >= 1024)
                    running.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C or duration elapsed; fall through and drain what is already in flight.
        }

        await Task.WhenAll(running.Where(t => !t.IsCanceled));
        pacer?.Dispose();
    }

    /// <summary>
    /// Sends one logical query, retrying while the limiter rejects it. A 429 is not an error: it is
    /// the limiter working, and the Retry-After it returns is how long the caller should wait.
    /// </summary>
    private async Task SendWithRetriesAsync(CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var outcome = await SendOnceAsync(attempt, token);

            if (outcome.Status is not HttpStatusCode.TooManyRequests)
                return;

            if (attempt >= options.MaxRetries)
            {
                metrics.RecordExhausted();
                return;
            }

            var delay = ResolveRetryDelay(outcome.RetryAfter, attempt);
            metrics.RecordRetryScheduled(delay);

            using var _ = metrics.TrackRetryWait();
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<Outcome> SendOnceAsync(int attempt, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _path)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Query-Priority", options.Priority);

        var started = Stopwatch.GetTimestamp();
        metrics.RequestStarted();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                metrics.RecordLimited(elapsed, await DescribeLimitAsync(response, token));
                return new Outcome(response.StatusCode, response.Headers.RetryAfter?.Delta);
            }

            if (response.IsSuccessStatusCode)
                metrics.RecordAllowed(elapsed, attempt > 0);
            else
                metrics.RecordError($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            return new Outcome(response.StatusCode, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome(null, null);
        }
        catch (Exception e)
        {
            metrics.RecordError(e.GetBaseException().Message);
            return new Outcome(null, null);
        }
        finally
        {
            metrics.RequestFinished();
        }
    }

    /// <summary>Reads the limiter's own explanation out of the problem+json body.</summary>
    private static async Task<string> DescribeLimitAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            var outcome = root.TryGetProperty("outcome", out var o) ? o.GetString() : null;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            return outcome is null ? detail ?? "rate limited" : $"{outcome}: {detail}";
        }
        catch
        {
            return "rate limited";
        }
    }

    private TimeSpan ResolveRetryDelay(TimeSpan? retryAfter, int attempt)
    {
        // Prefer the server's Retry-After. Fall back to exponential backoff, and jitter either way
        // so that a wave of rejected clients does not retry in lockstep and collide again.
        var baseDelay = retryAfter ?? TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
        if (baseDelay > options.MaxRetryDelay)
            baseDelay = options.MaxRetryDelay;

        var jitter = Random.Shared.NextDouble() * 0.3 + 0.85;   // 0.85x - 1.15x
        return baseDelay * jitter;
    }

    private readonly record struct Outcome(HttpStatusCode? Status, TimeSpan? RetryAfter);
}
