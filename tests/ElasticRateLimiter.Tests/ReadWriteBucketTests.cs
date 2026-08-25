using System.Threading.Tasks;
using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.Models;
using ElasticRateLimiter.Core.RateLimiting;
using Xunit;

namespace ElasticRateLimiter.Tests
{
    public class ReadWriteBucketTests
    {
        [Fact]
        public async Task WriteOperations_DefaultToUnlimited()
        {
            var rule = new IndexRateLimitRule
            {
                IndexPattern = "products",
                ReadCapacity = 5,
                ReadRefillRatePerSecond = 0,
                ReservedTokens = 0,
                WriteIsUnlimited = true // Default
            };

            var manager = new IndexPriorityTokenBucketManager([rule]);

            // Send 100 write operations
            for (int i = 0; i < 100; i++)
            {
                var writeResult = await manager.TryAcquireTokensAsync(["products"], OperationType.Write, 50, QueryPriority.Normal);
                Assert.True(writeResult.IsAllowed);
            }

            // Read operations should still be rate limited based on ReadCapacity
            var readRes1 = await manager.TryAcquireTokensAsync(["products"], OperationType.Read, 5, QueryPriority.Normal);
            Assert.True(readRes1.IsAllowed);

            var readRes2 = await manager.TryAcquireTokensAsync(["products"], OperationType.Read, 5, QueryPriority.Normal);
            Assert.False(readRes2.IsAllowed);
        }

        [Fact]
        public async Task WriteOperations_EnforceConfiguredLimit_WhenNotUnlimited()
        {
            var rule = new IndexRateLimitRule
            {
                IndexPattern = "audit",
                WriteCapacity = 10,
                WriteRefillRatePerSecond = 0,
                WriteIsUnlimited = false // Explicitly limited
            };

            var manager = new IndexPriorityTokenBucketManager([rule]);

            var res1 = await manager.TryAcquireTokensAsync(["audit"], OperationType.Write, 10, QueryPriority.Normal);
            Assert.True(res1.IsAllowed);

            var res2 = await manager.TryAcquireTokensAsync(["audit"], OperationType.Write, 5, QueryPriority.Normal);
            Assert.False(res2.IsAllowed);
        }
    }
}
