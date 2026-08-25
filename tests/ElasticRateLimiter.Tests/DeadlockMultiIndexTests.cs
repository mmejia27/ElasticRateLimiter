using System.Collections.Generic;
using System.Threading.Tasks;
using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.Models;
using ElasticRateLimiter.Core.RateLimiting;
using Xunit;

namespace ElasticRateLimiter.Tests
{
    public class DeadlockMultiIndexTests
    {
        [Fact]
        public async Task ConcurrentMultiIndexRequests_DoNotDeadlock()
        {
            var rule1 = new IndexRateLimitRule { IndexPattern = "idx-a", ReadCapacity = 100, ReadRefillRatePerSecond = 50 };
            var rule2 = new IndexRateLimitRule { IndexPattern = "idx-b", ReadCapacity = 100, ReadRefillRatePerSecond = 50 };
            var manager = new IndexPriorityTokenBucketManager([rule1, rule2]);

            var tasks = new List<Task<RateLimitResult>>();

            for (int i = 0; i < 20; i++)
            {
                if (i % 2 == 0)
                {
                    tasks.Add(Task.Run(() => manager.TryAcquireTokensAsync(["idx-a", "idx-b"], OperationType.Read, 2, QueryPriority.Normal)));
                }
                else
                {
                    tasks.Add(Task.Run(() => manager.TryAcquireTokensAsync(["idx-b", "idx-a"], OperationType.Read, 2, QueryPriority.Normal)));
                }
            }

            var results = await Task.WhenAll(tasks);
            Assert.Equal(20, results.Length);
            foreach (var res in results)
            {
                Assert.True(res.IsAllowed);
            }
        }

        [Fact]
        public async Task MultiIndex_AllOrNothing_RollsBackIfAnyIndexDeficient()
        {
            var rule1 = new IndexRateLimitRule { IndexPattern = "idx-1", ReadCapacity = 5, ReadRefillRatePerSecond = 0, ReservedTokens = 0 };
            var rule2 = new IndexRateLimitRule { IndexPattern = "idx-2", ReadCapacity = 20, ReadRefillRatePerSecond = 0, ReservedTokens = 0 };
            var manager = new IndexPriorityTokenBucketManager([rule1, rule2]);

            // Request 10 tokens from [idx-1, idx-2]. idx-1 only has 5 -> should fail all-or-nothing
            var result = await manager.TryAcquireTokensAsync(["idx-1", "idx-2"], OperationType.Read, 10, QueryPriority.Normal);
            Assert.False(result.IsAllowed);

            // Verify idx-2 tokens were NOT partially deducted
            var idx2Result = await manager.TryAcquireTokensAsync(["idx-2"], OperationType.Read, 20, QueryPriority.Normal);
            Assert.True(idx2Result.IsAllowed);
        }
    }
}
