using System;
using System.Collections.Generic;
using System.Text;

using ElasticRateLimiter.Core.Models;

namespace ElasticRateLimiter.Core.Operation
{
    public static class ElasticsearchOperationClassifier
    {
        public static OperationType Classify(string httpMethod, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return OperationType.Read;

            string method = httpMethod.ToUpperInvariant();
            string lowerPath = path.ToLowerInvariant();

            // Write methods
            if (method == "PUT" || method == "DELETE" || method == "PATCH")
            {
                return OperationType.Write;
            }

            if (method == "POST")
            {
                // Read operations routed via POST in Elasticsearch
                if (lowerPath.Contains("_search") ||
                    lowerPath.Contains("_msearch") ||
                    lowerPath.Contains("_count") ||
                    lowerPath.Contains("_explain") ||
                    lowerPath.Contains("_validate") ||
                    lowerPath.Contains("_termsenum"))
                {
                    return OperationType.Read;
                }

                // Default POST operations (indexing, _doc, _bulk, _update) -> Write
                return OperationType.Write;
            }

            // GET / HEAD -> Read
            return OperationType.Read;
        }
    }
}
