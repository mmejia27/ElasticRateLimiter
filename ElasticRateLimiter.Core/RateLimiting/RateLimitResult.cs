using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.RateLimiting
{
    public enum RateLimitOutcome
    {
        Allowed,
        RateLimited_InsufficientTokens,
        RateLimited_PriorityThreshold,
        RateLimited_QueueTimeout
    }

    public class RateLimitResult
    {
        public bool IsAllowed { get; set; }
        public RateLimitOutcome Outcome { get; set; } = RateLimitOutcome.Allowed;
        public string OutcomeReason { get; set; } = string.Empty;
        public long RemainingTokens { get; set; }
        public int TokenAcquireDurationMs { get; set; }
        public long RequiredTokens { get; set; }
        public string TargetIndices { get; set; } = string.Empty;

        public static RateLimitResult Success(int requiredTokens, long remaining, int durationMs, string indices) =>
            new()
            {
                IsAllowed = true,
                Outcome = RateLimitOutcome.Allowed,
                OutcomeReason = "Tokens granted",
                RequiredTokens = requiredTokens,
                RemainingTokens = remaining,
                TokenAcquireDurationMs = durationMs,
                TargetIndices = indices
            };

        public static RateLimitResult Denied(RateLimitOutcome outcome, string reason, long requiredTokens, long remaining, int durationMs, string indices) =>
            new()
            {
                IsAllowed = false,
                Outcome = outcome,
                OutcomeReason = reason,
                RequiredTokens = requiredTokens,
                RemainingTokens = remaining,
                TokenAcquireDurationMs = durationMs,
                TargetIndices = indices
            };
    }
}
