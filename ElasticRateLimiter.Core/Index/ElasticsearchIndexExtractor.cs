using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.Index
{
    public interface IElasticsearchIndexExtractor
    {
        IReadOnlyList<string> ExtractIndices(string path, string? queryParams = null);
    }

    public class ElasticsearchIndexExtractor : IElasticsearchIndexExtractor
    {
        public const string DefaultIndex = "_default";

        public IReadOnlyList<string> ExtractIndices(string path, string? queryParams = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new[] { DefaultIndex };

            string cleanPath = path.Trim('/');
            if (string.IsNullOrWhiteSpace(cleanPath))
                return new[] { DefaultIndex };

            string[] segments = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return new[] { DefaultIndex };

            string firstSegment = segments[0];

            // If path starts with system endpoint like _search, _msearch, _bulk, _count
            if (firstSegment.StartsWith('_'))
            {
                return new[] { DefaultIndex };
            }

            // Path format: /{index}/_search or /{index1,index2}/_search
            string indexPart = firstSegment;
            string[] rawIndices = indexPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (rawIndices.Length == 0)
                return new[] { DefaultIndex };

            // Sort lexicographically to prevent deadlocks when locking multi-index resources
            Array.Sort(rawIndices, StringComparer.Ordinal);
            return rawIndices.Distinct(StringComparer.Ordinal).ToArray();
        }
    }
}
