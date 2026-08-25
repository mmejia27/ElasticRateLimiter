using System.Threading.Tasks;
using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.Models;
using ElasticRateLimiter.Core.RateLimiting;
using Xunit;

namespace ElasticRateLimiter.Tests
{
    public class PriorityQueueTests
    {
        [Fact]
        public async Task LowPriorityQuery_RejectedWhenBelowReservedThreshold()
        {
            var rule = new IndexRateLimitRule
            {
                IndexPattern = "logs",
                ReadCapacity = 20,
                ReadRefillRatePerSecond = 1,
                ReservedTokens = 15 // Reserves 15 tokens for High/Critical
            };

            var manager = new IndexPriorityTokenBucketManager(new[] { rule });

            // Drain 10 tokens out of 20
            var res1 = await manager.TryAcquireTokensAsync(new[] { "logs" }, OperationType.Read, 10, QueryPriority.High);
            Assert.True(res1.IsAllowed);

            // Now 10 tokens remain. Low priority query needs 5 tokens, which drops available tokens to 5 (< 15 reserved threshold)
            var res2 = await manager.TryAcquireTokensAsync(new[] { "logs" }, OperationType.Read, 5, QueryPriority.Low);
            Assert.False(res2.IsAllowed);
            Assert.Equal(RateLimitOutcome.RateLimited_PriorityThreshold, res2.Outcome);

            // High priority query should be allowed
            var res3 = await manager.TryAcquireTokensAsync(new[] { "logs" }, OperationType.Read, 5, QueryPriority.High);
            Assert.True(res3.IsAllowed);
        }
    }
}
