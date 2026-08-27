namespace ElasticRateLimiter.LoadTest;

/// <summary>Shape of the Elasticsearch query to send, which drives its estimated token cost.</summary>
public enum QueryShape
{
    /// <summary>A plain term query. Costs 1 token.</summary>
    Simple,

    /// <summary>Adds an aggregation. Costs 6 tokens.</summary>
    Aggs,

    /// <summary>A wildcard query. Costs 11 tokens, so it drains a bucket roughly 11x faster.</summary>
    Wildcard,

    /// <summary>A script query with deep pagination. The most expensive shape.</summary>
    Expensive,
}

public sealed record LoadTestOptions
{
    public Uri Target { get; init; } = new("http://localhost:8081");
    public string Index { get; init; } = "load-test";
    public int Concurrency { get; init; } = 16;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Requests started per second across all workers. Zero means as fast as possible.</summary>
    public int RequestsPerSecond { get; init; } = 60;

    public QueryShape Shape { get; init; } = QueryShape.Simple;

    /// <summary>Low and Normal are rejected immediately; High and Critical wait for tokens.</summary>
    public string Priority { get; init; } = "Normal";

    /// <summary>Attempts after the first before a request is abandoned. Zero disables retrying.</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>Ceiling for the Retry-After / backoff delay between attempts.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReportInterval { get; init; } = TimeSpan.FromSeconds(1);

    public static LoadTestOptions Parse(string[] args)
    {
        var options = new LoadTestOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "-h" or "--help") throw new HelpRequestedException();

            if (!key.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{key}'.");

            if (i + 1 >= args.Length)
                throw new ArgumentException($"Option '{key}' needs a value.");

            var value = args[++i];
            options = key switch
            {
                "--target" => options with { Target = new Uri(value, UriKind.Absolute) },
                "--index" => options with { Index = value },
                "--concurrency" => options with { Concurrency = ParsePositive(key, value) },
                "--duration" => options with { Duration = TimeSpan.FromSeconds(ParsePositive(key, value)) },
                "--rps" => options with { RequestsPerSecond = int.Parse(value) },
                "--shape" => options with { Shape = Enum.Parse<QueryShape>(value, ignoreCase: true) },
                "--priority" => options with { Priority = value },
                "--max-retries" => options with { MaxRetries = int.Parse(value) },
                _ => throw new ArgumentException($"Unknown option '{key}'."),
            };
        }

        return options;
    }

    private static int ParsePositive(string key, string value)
        => int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"Option '{key}' needs a positive number, got '{value}'.");

    public static string Usage => """
        Sends Elasticsearch-shaped queries at ElasticRateLimiter until the rate limiter pushes back.

          --target       Rate limiter base URL          (default http://localhost:8081)
          --index        Index to query                 (default load-test)
          --concurrency  Requests in flight at once     (default 16)
          --duration     Seconds to run                 (default 20)
          --rps          Requests started per second,
                         0 for unthrottled              (default 60)
          --shape        simple | aggs | wildcard | expensive   (default simple)
                         Cost per query: 1 | 6 | 11 | 36
          --priority     Low | Normal | High | Critical, or 1-100  (default Normal)
                         High and Critical wait for tokens instead of failing fast.
          --max-retries  Retries after a 429            (default 5, 0 disables)

        A node's bucket holds the rule's capacity divided by the cluster size, and refills at the
        rule's rate divided by the same. Check GET /rules for the capacity actually in force - if
        the run finishes without ever being throttled, the offered load was below the refill rate.

        Example - drain a bucket quickly with expensive queries and watch the retries:
          dotnet run -- --shape wildcard --rps 60 --duration 30
        """;
}

public sealed class HelpRequestedException : Exception;
