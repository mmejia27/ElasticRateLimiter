using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ElasticRateLimiter.Core.Cost
{
    public class ElasticsearchQueryCostEstimator : IElasticsearchQueryCostEstimator
    {
        public QueryCostEstimate EstimateCost(ReadOnlySpan<byte> utf8BodyJson, string path)
        {
            if (utf8BodyJson.IsEmpty || utf8BodyJson.Length == 0)
            {
                return new QueryCostEstimate
                {
                    TotalTokensRequired = 1,
                    BaseCost = 1,
                    Summary = "Empty body default cost"
                };
            }

            string lowerPath = path.ToLowerInvariant();
            if (lowerPath.Contains("_msearch"))
            {
                return EstimateMSearchCost(utf8BodyJson);
            }

            try
            {
                var reader = new Utf8JsonReader(utf8BodyJson);
                return ParseSingleQueryCost(ref reader);
            }
            catch (Exception)
            {
                // Fallback to safe default on malformed JSON
                return new QueryCostEstimate
                {
                    TotalTokensRequired = 2,
                    BaseCost = 1,
                    Summary = "Fallback estimated cost (unparseable payload)"
                };
            }
        }

        private QueryCostEstimate ParseSingleQueryCost(ref Utf8JsonReader reader)
        {
            int baseCost = 1;
            int complexityScore = 0;
            int aggsScore = 0;
            int paginationPenalty = 0;

            int currentDepth = 0;
            string lastProperty = string.Empty;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        currentDepth++;
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        currentDepth--;
                        break;

                    case JsonTokenType.PropertyName:
                        lastProperty = reader.GetString() ?? string.Empty;
                        string propLower = lastProperty.ToLowerInvariant();

                        // Query types scoring
                        if (propLower == "wildcard" || propLower == "regexp")
                        {
                            complexityScore += 10;
                        }
                        else if (propLower == "fuzzy" || propLower == "prefix")
                        {
                            complexityScore += 5;
                        }
                        else if (propLower == "script" || propLower == "script_score" || propLower == "script_fields")
                        {
                            complexityScore += 15;
                        }
                        else if (propLower == "aggs" || propLower == "aggregations")
                        {
                            aggsScore += 5;
                        }
                        else if (propLower == "cardinality")
                        {
                            aggsScore += 8;
                        }
                        break;

                    case JsonTokenType.Number:
                        if (lastProperty.Equals("from", StringComparison.OrdinalIgnoreCase))
                        {
                            if (reader.TryGetInt32(out int fromVal))
                            {
                                if (fromVal > 10000) paginationPenalty += 20;
                                else if (fromVal > 1000) paginationPenalty += 5;
                            }
                        }
                        else if (lastProperty.Equals("size", StringComparison.OrdinalIgnoreCase))
                        {
                            if (reader.TryGetInt32(out int sizeVal))
                            {
                                if (sizeVal > 5000) paginationPenalty += 10;
                                else if (sizeVal > 1000) paginationPenalty += 3;
                            }
                        }
                        break;
                }
            }

            int total = Math.Max(1, baseCost + complexityScore + aggsScore + paginationPenalty);
            return new QueryCostEstimate
            {
                BaseCost = baseCost,
                QueryComplexityScore = complexityScore,
                AggregationScore = aggsScore,
                PaginationPenalty = paginationPenalty,
                TotalTokensRequired = total,
                Summary = $"Query Tokens: {total} (Base:{baseCost}, Complexity:{complexityScore}, Aggs:{aggsScore}, Pagination:{paginationPenalty})"
            };
        }

        private QueryCostEstimate EstimateMSearchCost(ReadOnlySpan<byte> utf8BodyJson)
        {
            string content = Encoding.UTF8.GetString(utf8BodyJson);
            string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            int subQueries = 0;
            int totalTokens = 0;

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("{") && trimmed.Contains("\"query\""))
                {
                    subQueries++;
                    byte[] bytes = Encoding.UTF8.GetBytes(trimmed);
                    var estimate = EstimateCost(bytes, "_search");
                    totalTokens += estimate.TotalTokensRequired;
                }
            }

            totalTokens = Math.Max(subQueries, totalTokens);
            return new QueryCostEstimate
            {
                BaseCost = Math.Max(1, subQueries),
                MultiSearchMultiplier = Math.Max(1, subQueries),
                TotalTokensRequired = totalTokens,
                Summary = $"MSearch Batch: {subQueries} queries, total tokens: {totalTokens}"
            };
        }
    }
}

