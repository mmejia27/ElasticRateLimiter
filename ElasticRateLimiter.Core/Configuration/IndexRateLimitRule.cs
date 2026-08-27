using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.Configuration
{
    public class IndexRateLimitRule
    {
        public string IndexPattern { get; set; } = "_default";

        // Read Bucket Settings
        public long ReadCapacity { get; set; } = 1000;
        public int ReadRefillRatePerSecond { get; set; } = 30;

        // Write Bucket Settings (Default Unlimited)
        public long WriteCapacity { get; set; } = long.MaxValue;
        public int WriteRefillRatePerSecond { get; set; } = int.MaxValue;
        public bool WriteIsUnlimited { get; set; } = true;

        // Priority & Headroom Settings
        public int ReservedTokens { get; set; } = 50;
        public int QueueTimeoutMs { get; set; } = 500;

        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
