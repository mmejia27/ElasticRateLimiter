using ElasticRateLimiter.Core.Common;
using Xunit;

namespace ElasticRateLimiter.Tests
{
    public class CorrelationIdTests
    {
        [Fact]
        public void GenerateShortCorrelationId_Returns22CharacterUrlSafeString()
        {
            var id = CorrelationIdGenerator.GenerateShortCorrelationId();

            Assert.NotNull(id);
            Assert.Equal(22, id.Length);
            Assert.DoesNotContain("+", id);
            Assert.DoesNotContain("/", id);
            Assert.DoesNotContain("=", id);
        }
    }
}
