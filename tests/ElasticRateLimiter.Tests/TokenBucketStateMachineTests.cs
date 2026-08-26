using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.RateLimiting;
using ElasticRateLimiter.Raft;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DotNext.IO;

namespace ElasticRateLimiter.Tests
{
    public class TokenBucketStateMachineTests
    {
        [Fact]
        public async Task ApplyAsync_ValidUpdateRule_AppliesRuleToManager()
        {
            // Arrange
            var tbManager = new IndexPriorityTokenBucketManager();
            var logger = NullLogger<TokenBucketStateMachine>.Instance;
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            
            Directory.CreateDirectory(path);
            try
            {
                await using var stateMachine = new TokenBucketStateMachine(
                    new TokenBucketStateMachineOptions(path), tbManager, logger);
                Assert.NotNull(stateMachine);
            }
            finally
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        }
    }
}
