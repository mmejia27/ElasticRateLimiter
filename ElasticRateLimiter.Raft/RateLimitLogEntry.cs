using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using ElasticRateLimiter.Core.Configuration;

namespace ElasticRateLimiter.Raft
{
    public class RateLimitLogEntry
    {
        public string CommandType { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;

        public static RateLimitLogEntry CreateUpdateRule(IndexRateLimitRule rule)
        {
            return new RateLimitLogEntry
            {
                CommandType = "UpdateRule",
                PayloadJson = JsonSerializer.Serialize(rule)
            };
        }

        public ReadOnlyMemory<byte> ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this);
    }
}
