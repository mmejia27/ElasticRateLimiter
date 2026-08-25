using ElasticRateLimiter.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.RateLimiting
{
    public class TokenBucket(Func<int> clusterSizeProvider, long capacity, int refillRate, int reservedTokens = 0, bool isUnlimited = false)
    {
        internal readonly Lock SyncRoot = new();
        private long _availableTokens;
        private DateTime _lastRefillUtc = DateTime.UtcNow;

        private readonly Func<int> _clusterSize = clusterSizeProvider ?? (() => 1);
        private long _baseCapacity = capacity;
        private int _baseRefillRate = refillRate;
        private int _baseReservedTokens = reservedTokens;

        public long Capacity => _baseCapacity > 0 ? Math.Max(1, _baseCapacity / _clusterSize()) : 0;
        public int RefillRate => _baseRefillRate > 0 ? Math.Max(1, _baseRefillRate / _clusterSize()) : 0;
        public int ReservedTokens => _baseReservedTokens > 0 ? Math.Max(1, _baseReservedTokens / _clusterSize()) : 0;
        public bool IsUnlimited { get; private set; } = isUnlimited;

        public void UpdateConfiguration(long capacity, int refillRate, int reservedTokens, bool isUnlimited)
        {
            _baseCapacity = capacity;
            _baseRefillRate = refillRate;
            _baseReservedTokens = reservedTokens;
            IsUnlimited = isUnlimited;
            if (_availableTokens > capacity && !isUnlimited)
            {
                _availableTokens = capacity;
                _lastRefillUtc = DateTime.UtcNow;
            }
        }

        private void Refill()
        {
            if (IsUnlimited) return;

            var now = DateTime.UtcNow;
            var elapsedSeconds = (long)(now - _lastRefillUtc).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                var tokensToAdd = elapsedSeconds * RefillRate;
                _availableTokens = Math.Min(Capacity, _availableTokens + tokensToAdd);
                _lastRefillUtc = now;
            }
        }

        public long GetAvailableTokens()
        {
            using (SyncRoot.EnterScope())
            {
                if (IsUnlimited) return long.MaxValue;
                Refill();
                return _availableTokens;
            }
        }
        public bool CanConsume(long requiredTokens, QueryPriority priority, out long remainingTokens, out string reason)
        {
            using (SyncRoot.EnterScope())
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
                    reason = $"Insufficient tokens. Required: {requiredTokens}, Available: {_availableTokens:F1}";
                    return false;
                }

                if (priority.Level < QueryPriorityLevel.High && (_availableTokens - requiredTokens) < ReservedTokens)
                {
                    remainingTokens = _availableTokens;
                    reason = $"Priority threshold restriction. Reserved tokens: {ReservedTokens}, Available: {_availableTokens:F1}";
                    return false;
                }

                remainingTokens = _availableTokens;
                reason = "Tokens available";
                return true;
            }
        }

        public bool TryConsume(long requiredTokens, QueryPriority priority, out long remainingTokens, out string reason)
        {

            using (SyncRoot.EnterScope())
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
}
