using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ElasticRateLimiter.Core.Common;

namespace ElasticRateLimiter.Middleware
{
    public class ElasticsearchRateLimiterDelegatingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains(CorrelationIdGenerator.HeaderName))
            {
                string correlationId = CorrelationIdGenerator.GenerateShortCorrelationId();
                request.Headers.Add(CorrelationIdGenerator.HeaderName, correlationId);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
