using ElasticRateLimiter.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.RateLimiting
{
    public class TokenBucket
    {
        private long _availableTokens;
        private DateTime _lastRefillUTC;
        
        public long Capacity { get; private set; }
        public int RefillRatePerSecond { get; private set; }
        public int ReservedTokens { get; private set; }
        public bool IsUnlimited { get; private set; }

        public TokenBucket(long capacity, int  refillRatePerSecond, int reservedTokens = 0, bool isUnlimited = false)
        {
            Capacity = capacity;
            RefillRatePerSecond = refillRatePerSecond;
            ReservedTokens = reservedTokens;
            IsUnlimited = isUnlimited;
            _lastRefillUTC = DateTime.UtcNow;
        }

        public void UpdateConfiguration(long capacity, int refillRate, bool isUnlimited, int reservedTokens)
        {
            Capacity = capacity;
            RefillRatePerSecond = refillRate;
            IsUnlimited = isUnlimited;
            ReservedTokens = reservedTokens;
            if (_availableTokens > capacity && !isUnlimited)
            {
                _availableTokens = capacity;
                _lastRefillUTC = DateTime.UtcNow;
            }
        }

        private void Refill()
        {
            if (IsUnlimited) return;

            var now = DateTime.UtcNow;
            var elapsedSeconds = (long)(now - _lastRefillUTC).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                var tokensToAdd = elapsedSeconds * RefillRatePerSecond;
                _availableTokens = Math.Min(Capacity, _availableTokens + tokensToAdd);
                _lastRefillUTC = now;
            }
        }

        public bool TryConsume(long requiredTokens, QueryPriority priority, out long remainingTokens, out string reason)
        {
            if (IsUnlimited)
            {
                remainingTokens = long.MaxValue;
                reason = "Unlimited bucket";
                return true;
            }

            Refill();

            if (_availableTokens < requiredTokens)
            {
                remainingTokens = _availableTokens;
                reason = $"Insufficient tokens. Required: {requiredTokens}, Available: {_availableTokens}";
                return false;
            }

            // Check reserve threshold
            // Low priority queries cannot consume if remaining tokens drop below ReservedTokens
            if (priority.Level < QueryPriorityLevel.High && (_availableTokens - requiredTokens) < ReservedTokens)
            {
                remainingTokens = _availableTokens;
                reason = $"Priority threshold restriction. Reserved tokens: {ReservedTokens}, Available: {_availableTokens}";
                return false;
            }

            _availableTokens -= requiredTokens;
            remainingTokens = _availableTokens;
            reason = "Tokens granted";
            return true;
        }

    } 
}
