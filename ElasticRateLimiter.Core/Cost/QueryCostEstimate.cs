using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.Cost
{
    public class QueryCostEstimate
    {
        public int TotalTokensRequired { get; set; } = 1;
        public int BaseCost { get; set; } = 1;
        public int QueryComplexityScore { get; set; } = 0;
        public int AggregationScore { get; set; } = 0;
        public int PaginationPenalty { get; set; } = 0;
        public int MultiSearchMultiplier { get; set; } = 1;
        public string Summary { get; set; } = "Simple query";
    }
}
