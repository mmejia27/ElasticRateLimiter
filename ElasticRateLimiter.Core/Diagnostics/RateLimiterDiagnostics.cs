using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ElasticRateLimiter.Core.Diagnostics
{
    public static class RateLimiterDiagnostics
    {
        public const string ActivitySourceName = "ElasticRateLimiter";
        public const string MeterName = "ElasticRateLimiter";

        public static readonly ActivitySource Source = new ActivitySource(ActivitySourceName, "1.0.0");
        public static readonly Meter Meter = new Meter(MeterName, "1.0.0");

        public static readonly Counter<long> TokensConsumedCounter = Meter.CreateCounter<long>(
            "es_rate_limiter_tokens_consumed_total",
            description: "Total tokens consumed by index and query priority.");

        public static readonly Counter<long> RateLimitedRequestsCounter = Meter.CreateCounter<long>(
            "es_rate_limiter_requests_rate_limited_total",
            description: "Total request rejections due to rate limiting.");

        public static readonly Histogram<double> TokenAcquireDurationHistogram = Meter.CreateHistogram<double>(
            "es_rate_limiter_token_acquire_duration_ms",
            unit: "ms",
            description: "Time taken to acquire rate limit tokens.");

        public static Activity? StartActivity(string name, string correlationId, string[] indices, string priority, string operation)
        {
            var activity = Source.StartActivity(name, ActivityKind.Server);
            if (activity != null)
            {
                activity.SetTag("correlation.id", correlationId);
                activity.SetTag("es.index", string.Join(",", indices));
                activity.SetTag("es.priority", priority);
                activity.SetTag("es.operation", operation);
            }
            return activity;
        }
    }
}