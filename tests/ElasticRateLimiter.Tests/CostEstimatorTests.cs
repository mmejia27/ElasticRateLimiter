using System.Text;
using ElasticRateLimiter.Core.Cost;
using Xunit;

namespace ElasticRateLimiter.Tests
{
    public class CostEstimatorTests
    {
        private readonly ElasticsearchQueryCostEstimator _estimator = new();

        [Fact]
        public void SimpleMatchQuery_ReturnsBaseCostOne()
        {
            var json = "{\"query\":{\"match\":{\"message\":\"test\"}}}";
            var bytes = Encoding.UTF8.GetBytes(json);

            var estimate = _estimator.EstimateCost(bytes, "/orders/_search");

            Assert.Equal(1, estimate.TotalTokensRequired);
            Assert.Equal(1, estimate.BaseCost);
        }

        [Fact]
        public void WildcardAndAggsQuery_ReturnsHighCost()
        {
            var json = "{\"query\":{\"wildcard\":{\"user\":\"user_*\"}},\"aggs\":{\"top_users\":{\"terms\":{\"field\":\"user\"}}}}";
            var bytes = Encoding.UTF8.GetBytes(json);

            var estimate = _estimator.EstimateCost(bytes, "/orders/_search");

            Assert.True(estimate.TotalTokensRequired > 10);
            Assert.Equal(10, estimate.QueryComplexityScore);
            Assert.Equal(5, estimate.AggregationScore);
        }

        [Fact]
        public void HighPaginationFrom_ReturnsPaginationPenalty()
        {
            var json = "{\"from\":15000,\"size\":1000,\"query\":{\"match_all\":{}}}";
            var bytes = Encoding.UTF8.GetBytes(json);

            var estimate = _estimator.EstimateCost(bytes, "/orders/_search");

            Assert.True(estimate.PaginationPenalty >= 20);
            Assert.True(estimate.TotalTokensRequired >= 21);
        }

        [Fact]
        public void MSearchBatchQuery_SumsSubQueryTokens()
        {
            var msearchPayload = "{}\n{\"query\":{\"match_all\":{}}}\n{}\n{\"query\":{\"wildcard\":{\"tag\":\"admin_*\"}}}\n";
            var bytes = Encoding.UTF8.GetBytes(msearchPayload);

            var estimate = _estimator.EstimateCost(bytes, "/_msearch");

            Assert.True(estimate.TotalTokensRequired >= 11);
            Assert.Equal(2, estimate.MultiSearchMultiplier);
        }
    }
}
