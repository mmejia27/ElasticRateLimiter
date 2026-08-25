using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.Configuration
{
    public class IndexRateLimitRule
    {
        public string IndexPattern { get; set; } = "_default";

        // Read Bucket Settings
        public long ReadCapacity { get; set; } = 100;
        public int ReadRefillRatePerSecond { get; set; } = 5;

        // Write Bucket Settings (Default Unlimited)
        public long WriteCapacity { get; set; } = int.MaxValue;
        public int WriteRefillRatePerSecond { get; set; } = int.MaxValue;
        public bool WriteIsUnlimited { get; set; } = true;

        // Priority & Headroom Settings
        public int ReservedPriorityTokens { get; set; } = 20;
        public int QueueTimeoutMs { get; set; } = 500;

        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
