using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Core.Models
{
    public enum QueryPriorityLevel
    {
        Low = 25,
        Normal = 50,
        High = 75,
        Critical = 100
    }
    public readonly struct QueryPriority : IEquatable<QueryPriority>, IComparable<QueryPriority>
    {
        public int Weight { get; }
        public QueryPriorityLevel Level { get; }

        public QueryPriority(int weight)
        {
            Weight = Math.Clamp(weight, 1, 100);
            Level = Weight switch
            {
                >= 90 => QueryPriorityLevel.Critical,
                >= 70 => QueryPriorityLevel.High,
                >= 40 => QueryPriorityLevel.Normal,
                _ => QueryPriorityLevel.Low
            };
        }

        public QueryPriority(QueryPriorityLevel level)
        {
            Level = level;
            Weight = (int)level;
        }

        public static QueryPriority Normal => new(QueryPriorityLevel.Normal);
        public static QueryPriority High => new(QueryPriorityLevel.High);
        public static QueryPriority Critical => new(QueryPriorityLevel.Critical);
        public static QueryPriority Low => new(QueryPriorityLevel.Low);

        public static QueryPriority Parse(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Normal;

            if (int.TryParse(input, out var weight))
                return new QueryPriority(weight);

            if (Enum.TryParse<QueryPriorityLevel>(input, true, out var level))
                return new QueryPriority(level);

            return Normal;
        }

        public int CompareTo(QueryPriority other) => Weight.CompareTo(other.Weight);
        public bool Equals(QueryPriority other) => Weight == other.Weight;
        public override bool Equals(object? obj) => obj is QueryPriority other && Equals(other);
        public override int GetHashCode() => Weight.GetHashCode();
        public override string ToString() => $"{Level} ({Weight})";

        public static bool operator ==(QueryPriority left, QueryPriority right) => left.Equals(right);
        public static bool operator !=(QueryPriority left, QueryPriority right) => !left.Equals(right);
        public static bool operator >(QueryPriority left, QueryPriority right) => left.Weight > right.Weight;
        public static bool operator <(QueryPriority left, QueryPriority right) => left.Weight < right.Weight;
    }
}
