using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.Diagnostics;
using ElasticRateLimiter.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ElasticRateLimiter.Core.RateLimiting
{
    public class IndexPriorityTokenBucketManager
    {
        private readonly ConcurrentDictionary<string, (TokenBucket Read, TokenBucket Write)> _buckets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, IndexRateLimitRule> _rules = new(StringComparer.Ordinal);
        private readonly Func<int> _clusterSizeProvider;

        public IndexPriorityTokenBucketManager(IEnumerable<IndexRateLimitRule>? initialRules = null)
            : this(null, initialRules)
        {
        }

        public IndexPriorityTokenBucketManager(Func<int>? clusterSizeProvider, IEnumerable<IndexRateLimitRule>? initialRules = null)
        {
            _clusterSizeProvider = clusterSizeProvider ?? (() => 1);

            var defaultRule = new IndexRateLimitRule
            {
                IndexPattern = "_default",
                ReadCapacity = 100,
                ReadRefillRatePerSecond = 5,
                WriteCapacity = long.MaxValue,
                WriteRefillRatePerSecond = int.MaxValue,
                WriteIsUnlimited = true,
                ReservedTokens = 20,
                QueueTimeoutMs = 500
            };
            ApplyRule(defaultRule);

            if (initialRules != null)
            {
                foreach (var rule in initialRules)
                {
                    ApplyRule(rule);
                }
            }
        }

        public void ApplyRule(IndexRateLimitRule rule)
        {
            string pattern = string.IsNullOrWhiteSpace(rule.IndexPattern) ? "_default" : rule.IndexPattern;
            _rules[pattern] = rule;

            var (readBucket, writeBucket) = _buckets.GetOrAdd(pattern, p => (
                new TokenBucket(_clusterSizeProvider, rule.ReadCapacity, rule.ReadRefillRatePerSecond, rule.ReservedTokens, false),
                new TokenBucket(_clusterSizeProvider, rule.WriteCapacity, rule.WriteRefillRatePerSecond, 0, rule.WriteIsUnlimited)
            ));

            readBucket.UpdateConfiguration(rule.ReadCapacity, rule.ReadRefillRatePerSecond, rule.ReservedTokens, false);
            writeBucket.UpdateConfiguration(rule.WriteCapacity, rule.WriteRefillRatePerSecond, 0, rule.WriteIsUnlimited);
        }

        public IEnumerable<IndexRateLimitRule> GetAllRules()
        {
            return _rules.Values;
        }

        public async Task<RateLimitResult> TryAcquireTokensAsync(
            IReadOnlyList<string> targetIndices,
            OperationType operationType,
            long requiredTokens,
            QueryPriority priority,
            int? overrideQueueTimeoutMs = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            string indicesStr = string.Join(",", targetIndices);
            int queueTimeout = overrideQueueTimeoutMs ?? ResolveQueueTimeout(targetIndices);

            // Ensure order to prevent deadlocks across concurrent multi-index requests
            var sortedIndices = targetIndices.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

            // Attempt atomic acquisition
            while (!cancellationToken.IsCancellationRequested)
            {
                if (TryAtomicAcquire(sortedIndices, operationType, requiredTokens, priority, out long minRemaining, out string denyReason))
                {
                    sw.Stop();
                    RateLimiterDiagnostics.TokensConsumedCounter.Add(requiredTokens,
                        new KeyValuePair<string, object?>("es.index", indicesStr),
                        new KeyValuePair<string, object?>("es.priority", priority.Level.ToString()));
                    RateLimiterDiagnostics.TokenAcquireDurationHistogram.Record(sw.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("es.index", indicesStr),
                        new KeyValuePair<string, object?>("outcome", "Allowed"));

                    return RateLimitResult.Success(requiredTokens, minRemaining, (int)sw.Elapsed.TotalMilliseconds, indicesStr);
                }

                // If low priority or timeout reached -> deny immediately
                if (priority.Level < QueryPriorityLevel.High || sw.ElapsedMilliseconds >= queueTimeout)
                {
                    sw.Stop();
                    var outcome = denyReason.Contains("Priority threshold")
                        ? RateLimitOutcome.RateLimited_PriorityThreshold
                        : (sw.ElapsedMilliseconds >= queueTimeout ? RateLimitOutcome.RateLimited_QueueTimeout : RateLimitOutcome.RateLimited_InsufficientTokens);

                    RateLimiterDiagnostics.RateLimitedRequestsCounter.Add(1,
                        new KeyValuePair<string, object?>("es.index", indicesStr),
                        new KeyValuePair<string, object?>("es.priority", priority.Level.ToString()),
                        new KeyValuePair<string, object?>("reason", outcome.ToString()));

                    return RateLimitResult.Denied(outcome, denyReason, requiredTokens, minRemaining, (int)sw.Elapsed.TotalMilliseconds, indicesStr);
                }

                // High/Critical priority query waits in async loop up to timeout
                int delayMs = Math.Min(25, (int)(queueTimeout - sw.ElapsedMilliseconds));
                if (delayMs <= 0) break;
                await Task.Delay(delayMs, cancellationToken);
            }

            sw.Stop();
            return RateLimitResult.Denied(RateLimitOutcome.RateLimited_QueueTimeout, "Priority queue wait timeout expired", requiredTokens, 0, (int)sw.Elapsed.TotalMilliseconds, indicesStr);
        }

        private bool TryAtomicAcquire(
            List<string> sortedIndices,
            OperationType operationType,
            long requiredTokens,
            QueryPriority priority,
            out long minRemainingTokens,
            out string denyReason)
        {
            var targetBuckets = new List<TokenBucket>(sortedIndices.Count);
            foreach (var index in sortedIndices)
            {
                var (r, w) = GetBucketsForIndex(index);
                targetBuckets.Add(operationType == OperationType.Read ? r : w);
            }

            int locksAcquired = 0;
            try
            {
                // Acquire locks in deterministic order (by alphabetically sorted index names) to prevent deadlocks
                for (int i = 0; i < targetBuckets.Count; i++)
                {
                    targetBuckets[i].SyncRoot.Enter();
                    locksAcquired++;
                }

                minRemainingTokens = long.MaxValue;
                denyReason = string.Empty;

                // Dry run
                for (int i = 0; i < sortedIndices.Count; i++)
                {
                    var index = sortedIndices[i];
                    var targetBucket = targetBuckets[i];

                    if (!targetBucket.CanConsume(requiredTokens, priority, out long remaining, out string reason))
                    {
                        denyReason = $"Index '{index}': {reason}";
                        minRemainingTokens = remaining;
                        return false;
                    }
                }

                // Deduct tokens
                for (int i = 0; i < sortedIndices.Count; i++)
                {
                    var targetBucket = targetBuckets[i];
                    targetBucket.TryConsume(requiredTokens, priority, out _, out _);
                }

                minRemainingTokens = targetBuckets.Select(b => b.GetAvailableTokens()).DefaultIfEmpty(0).Min();

                return true;
            }
            finally
            {
                // Release locks in reverse order
                for (int i = locksAcquired - 1; i >= 0; i--)
                {
                    targetBuckets[i].SyncRoot.Exit();
                }
            }
        }

        private (TokenBucket Read, TokenBucket Write) GetBucketsForIndex(string indexName)
        {
            return _buckets.GetOrAdd(indexName, idx =>
            {
                // Find matching rule by exact name or default
                if (_rules.TryGetValue(idx, out var exactRule))
                {
                    return (
                        new TokenBucket(_clusterSizeProvider, exactRule.ReadCapacity, exactRule.ReadRefillRatePerSecond, exactRule.ReservedTokens, false),
                        new TokenBucket(_clusterSizeProvider, exactRule.WriteCapacity, exactRule.WriteRefillRatePerSecond, 0, exactRule.WriteIsUnlimited)
                    );
                }

                // Fallback to _default rule
                var defaultRule = _rules.GetValueOrDefault("_default") ?? new IndexRateLimitRule();
                return (
                    new TokenBucket(_clusterSizeProvider, defaultRule.ReadCapacity, defaultRule.ReadRefillRatePerSecond, defaultRule.ReservedTokens, false),
                    new TokenBucket(_clusterSizeProvider, defaultRule.WriteCapacity, defaultRule.WriteRefillRatePerSecond, 0, defaultRule.WriteIsUnlimited)
                );
            });
        }

        private int ResolveQueueTimeout(IReadOnlyList<string> indices)
        {
            foreach (var index in indices)
            {
                if (_rules.TryGetValue(index, out var rule) && rule.QueueTimeoutMs > 0)
                {
                    return rule.QueueTimeoutMs;
                }
            }
            return _rules.GetValueOrDefault("_default")?.QueueTimeoutMs ?? 500;
        }
    }
}