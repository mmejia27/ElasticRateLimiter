using ElasticRateLimiter.LoadTest;

LoadTestOptions options;
try
{
    options = LoadTestOptions.Parse(args);
}
catch (HelpRequestedException)
{
    Console.WriteLine(LoadTestOptions.Usage);
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(LoadTestOptions.Usage);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;                 // Drain in flight requests and print the summary.
    cancellation.Cancel();
};

using var http = new HttpClient
{
    BaseAddress = options.Target,
    Timeout = TimeSpan.FromSeconds(30),
};
// The limiter's 429 is a normal outcome here, so let a large burst share connections freely.
http.DefaultRequestHeaders.ConnectionClose = false;

var metrics = new Metrics();
var reporter = new ConsoleReporter(options, metrics);
var runner = new LoadTestRunner(options, http, metrics);

Console.WriteLine($"Sending {options.Shape} queries (~{QueryBodies.CostOf(options.Shape)} tokens each) " +
                  $"to {options.Target}{options.Index}/_search");
Console.WriteLine($"concurrency {options.Concurrency}, {(options.RequestsPerSecond > 0 ? $"{options.RequestsPerSecond} rps" : "unthrottled")}, " +
                  $"priority {options.Priority}, {options.Duration.TotalSeconds:F0}s, up to {options.MaxRetries} retries");

if (!await ProbeAsync(http, options, cancellation.Token))
    return 1;

Console.WriteLine();

using var reporting = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
var reporterTask = reporter.RunAsync(reporting.Token);

await runner.RunAsync(cancellation.Token);

await reporting.CancelAsync();
await reporterTask;

reporter.WriteSummary();
return metrics.Errors > 0 && metrics.Allowed is 0 ? 1 : 0;

// Fail fast with a clear message rather than reporting thousands of connection errors.
static async Task<bool> ProbeAsync(HttpClient http, LoadTestOptions options, CancellationToken token)
{
    try
    {
        using var response = await http.GetAsync("/cluster", token);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"error: {options.Target} answered /cluster with HTTP {(int)response.StatusCode}.");
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(token);
        Console.WriteLine($"cluster: {body}");
        return true;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"error: cannot reach {options.Target} ({e.GetBaseException().Message}).");
        Console.Error.WriteLine("Is the rate limiter running? docker compose -f ElasticRateLimiter.Server/docker-compose.yml up -d");
        return false;
    }
}
