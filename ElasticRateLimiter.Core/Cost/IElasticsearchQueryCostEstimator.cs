using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.Cost
{
    public interface IElasticsearchQueryCostEstimator
    {
        QueryCostEstimate EstimateCost(ReadOnlySpan<byte> utf8BodyJson, string path);
    }
}
