using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ElasticRateLimiter.Core.Common;
using ElasticRateLimiter.Core.Cost;
using ElasticRateLimiter.Core.Diagnostics;
using ElasticRateLimiter.Core.Index;
using ElasticRateLimiter.Core.Models;
using ElasticRateLimiter.Core.Operation;
using ElasticRateLimiter.Core.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElasticRateLimiter.Middleware
{
    public class ElasticsearchRateLimiterOptions
    {
        public string ElasticsearchTargetUrl { get; set; } = "http://localhost:9200";
        public bool EnableReverseProxy { get; set; } = true;
        public int DefaultQueueTimeoutMs { get; set; } = 500;
    }

    public class ElasticsearchRateLimiterMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IElasticsearchQueryCostEstimator _costEstimator;
        private readonly IElasticsearchIndexExtractor _indexExtractor;
        private readonly IndexPriorityTokenBucketManager _tokenBucketManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptions<ElasticsearchRateLimiterOptions> _options;
        private readonly ILogger<ElasticsearchRateLimiterMiddleware> _logger;

        public ElasticsearchRateLimiterMiddleware(
            RequestDelegate next,
            IElasticsearchQueryCostEstimator costEstimator,
            IElasticsearchIndexExtractor indexExtractor,
            IndexPriorityTokenBucketManager tokenBucketManager,
            IHttpClientFactory httpClientFactory,
            IOptions<ElasticsearchRateLimiterOptions> options,
            ILogger<ElasticsearchRateLimiterMiddleware> logger)
        {
            _next = next;
            _costEstimator = costEstimator;
            _indexExtractor = indexExtractor;
            _tokenBucketManager = tokenBucketManager;
            _httpClientFactory = httpClientFactory;
            _options = options;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            string correlationId = ResolveCorrelationId(context);
            context.Response.Headers[CorrelationIdGenerator.HeaderName] = correlationId;

            string path = context.Request.Path.Value ?? "/";
            var targetIndices = _indexExtractor.ExtractIndices(path, context.Request.QueryString.Value);
            var operationType = ElasticsearchOperationClassifier.Classify(context.Request.Method, path);
            var priority = QueryPriority.Parse(context.Request.Headers["X-Query-Priority"]);
            int? overrideTimeout = ResolveTimeoutOverride(context);

            using var activity = RateLimiterDiagnostics.StartActivity(
                "Elasticsearch.RateLimiter",
                correlationId,
                targetIndices.ToArray(),
                priority.Level.ToString(),
                operationType.ToString());

            using (_logger.BeginScope("{CorrelationId}", correlationId))
            {
                // Buffer request body to estimate cost
                context.Request.EnableBuffering();
                byte[] bodyBytes = Array.Empty<byte>();

                if (context.Request.ContentLength > 0 && context.Request.Body.CanRead)
                {
                    using var ms = new MemoryStream();
                    await context.Request.Body.CopyToAsync(ms);
                    bodyBytes = ms.ToArray();
                    context.Request.Body.Position = 0;
                }

                var costEstimate = _costEstimator.EstimateCost(bodyBytes, path);
                activity?.SetTag("es.query_cost.total", costEstimate.TotalTokensRequired);

                var rateLimitResult = await _tokenBucketManager.TryAcquireTokensAsync(
                    targetIndices,
                    operationType,
                    costEstimate.TotalTokensRequired,
                    priority,
                    overrideTimeout,
                    context.RequestAborted);

                activity?.SetTag("es.rate_limit.outcome", rateLimitResult.Outcome.ToString());
                activity?.SetTag("es.rate_limit.token_acquire_duration_ms", rateLimitResult.TokenAcquireDurationMs);

                // Attach rate limit headers to response
                context.Response.Headers["X-RateLimit-Limit"] = costEstimate.TotalTokensRequired.ToString();
                context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, (int)rateLimitResult.RemainingTokens).ToString();
                context.Response.Headers["X-RateLimit-Cost"] = costEstimate.TotalTokensRequired.ToString();

                if (!rateLimitResult.IsAllowed)
                {
                    _logger.LogWarning("Request rate limited. Indices: {Indices}, Outcome: {Outcome}, Reason: {Reason}",
                        rateLimitResult.TargetIndices, rateLimitResult.Outcome, rateLimitResult.OutcomeReason);

                    context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                    context.Response.Headers["Retry-After"] = "1";
                    context.Response.ContentType = "application/problem+json";

                    var problemDetails = new
                    {
                        type = "https://httpstatuses.com/429",
                        title = "Too Many Requests",
                        status = 429,
                        detail = $"Elasticsearch request rate limited for indices [{rateLimitResult.TargetIndices}]. {rateLimitResult.OutcomeReason}",
                        correlationId = correlationId,
                        outcome = rateLimitResult.Outcome.ToString(),
                        cost = costEstimate.TotalTokensRequired
                    };

                    await context.Response.WriteAsync(JsonSerializer.Serialize(problemDetails));
                    return;
                }

                if (_options.Value.EnableReverseProxy)
                {
                    await ProxyRequestAsync(context, correlationId);
                }
                else
                {
                    await _next(context);
                }
            }
        }

        private async Task ProxyRequestAsync(HttpContext context, string correlationId)
        {
            var client = _httpClientFactory.CreateClient("ElasticsearchProxy");
            string targetUrl = $"{_options.Value.ElasticsearchTargetUrl.TrimEnd('/')}{context.Request.Path}{context.Request.QueryString}";

            using var proxyReq = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);

            // Copy request headers
            foreach (var header in context.Request.Headers)
            {
                if (!header.Key.StartsWith(':') && !header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    proxyReq.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            proxyReq.Headers.Remove(CorrelationIdGenerator.HeaderName);
            proxyReq.Headers.Add(CorrelationIdGenerator.HeaderName, correlationId);

            if (context.Request.ContentLength > 0 && context.Request.Body.CanRead)
            {
                var streamContent = new StreamContent(context.Request.Body);
                if (context.Request.ContentType != null)
                {
                    streamContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType);
                }
                proxyReq.Content = streamContent;
            }

            try
            {
                using var response = await client.SendAsync(proxyReq, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;

                foreach (var header in response.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
                foreach (var header in response.Content.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                await response.Content.CopyToAsync(context.Response.Body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to proxy request to target Elasticsearch instance");
                context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await context.Response.WriteAsync($"Gateway Error: Unable to connect to Elasticsearch target ({ex.Message})");
            }
        }

        private static string ResolveCorrelationId(HttpContext context)
        {
            if (context.Request.Headers.TryGetValue(CorrelationIdGenerator.HeaderName, out var id) && !string.IsNullOrWhiteSpace(id))
            {
                return id.ToString();
            }
            if (context.Request.Headers.TryGetValue(CorrelationIdGenerator.AltHeaderName, out var altId) && !string.IsNullOrWhiteSpace(altId))
            {
                return altId.ToString();
            }
            return CorrelationIdGenerator.GenerateShortCorrelationId();
        }

        private static int? ResolveTimeoutOverride(HttpContext context)
        {
            if (context.Request.Headers.TryGetValue("X-Query-Priority-Timeout-Ms", out var timeoutHeader) &&
                int.TryParse(timeoutHeader, out int timeoutMs))
            {
                return timeoutMs;
            }
            return null;
        }
    }
}
