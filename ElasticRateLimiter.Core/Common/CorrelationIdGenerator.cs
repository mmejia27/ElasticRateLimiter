using System;

namespace ElasticRateLimiter.Core.Common
{
    public static class CorrelationIdGenerator
    {
        public const string HeaderName = "X-Correlation-ID";
        public const string AltHeaderName = "X-Request-ID";

        public static string GenerateShortCorrelationId()
        {
            Span<byte> guidBytes = stackalloc byte[16];
            Guid.NewGuid().TryWriteBytes(guidBytes);

            // Base64 encode and convert to URL-safe (no +, /, or = padding)
            string base64 = Convert.ToBase64String(guidBytes);
            return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }
}
