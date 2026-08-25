using System;
using System.Collections.Generic;
using System.Data;
using ElasticRateLimiter.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace ElasticRateLimiter.Server.Storage
{
    public class SqliteIndexConfigurationRepository : IIndexConfigurationRepository
    {
        private readonly string _connectionString;

        public SqliteIndexConfigurationRepository(string dbPath = "ratelimiter.db")
        {
            _connectionString = $"Data Source={dbPath}";
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            string sql = @"
                CREATE TABLE IF NOT EXISTS IndexRateLimitRules (
                    IndexPattern TEXT PRIMARY KEY,
                    ReadCapacity INTEGER NOT NULL,
                    ReadRefillRatePerSecond INTEGER NOT NULL,
                    WriteCapacity INTEGER NOT NULL,
                    WriteRefillRatePerSecond INTEGER NOT NULL,
                    WriteIsUnlimited INTEGER NOT NULL,
                    ReservedTokens INTEGER NOT NULL,
                    QueueTimeoutMs INTEGER NOT NULL,
                    LastUpdatedUtc TEXT NOT NULL
                );
            ";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }

        public async Task<IReadOnlyList<IndexRateLimitRule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<IndexRateLimitRule>();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            string sql = "SELECT IndexPattern, ReadCapacity, ReadRefillRatePerSecond, WriteCapacity, WriteRefillRatePerSecond, WriteIsUnlimited, ReservedTokens, QueueTimeoutMs, LastUpdatedUtc FROM IndexRateLimitRules;";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(MapRule(reader));
            }

            return list;
        }

        public async Task<IndexRateLimitRule?> GetRuleForIndexAsync(string indexName, CancellationToken cancellationToken = default)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            string sql = "SELECT IndexPattern, ReadCapacity, ReadRefillRatePerSecond, WriteCapacity, WriteRefillRatePerSecond, WriteIsUnlimited, ReservedTokens, QueueTimeoutMs, LastUpdatedUtc FROM IndexRateLimitRules WHERE IndexPattern = @idx;";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@idx", indexName);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            if (await reader.ReadAsync(cancellationToken))
            {
                return MapRule(reader);
            }

            return null;
        }

        public async Task SaveOrUpdateRuleAsync(IndexRateLimitRule rule, CancellationToken cancellationToken = default)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            string sql = @"
                INSERT INTO IndexRateLimitRules (IndexPattern, ReadCapacity, ReadRefillRatePerSecond, WriteCapacity, WriteRefillRatePerSecond, WriteIsUnlimited, ReservedTokens, QueueTimeoutMs, LastUpdatedUtc)
                VALUES (@idx, @rc, @rr, @wc, @wr, @wu, @rt, @qt, @lu)
                ON CONFLICT(IndexPattern) DO UPDATE SET
                    ReadCapacity = excluded.ReadCapacity,
                    ReadRefillRatePerSecond = excluded.ReadRefillRatePerSecond,
                    WriteCapacity = excluded.WriteCapacity,
                    WriteRefillRatePerSecond = excluded.WriteRefillRatePerSecond,
                    WriteIsUnlimited = excluded.WriteIsUnlimited,
                    ReservedTokens = excluded.ReservedTokens,
                    QueueTimeoutMs = excluded.QueueTimeoutMs,
                    LastUpdatedUtc = excluded.LastUpdatedUtc;
            ";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@idx", rule.IndexPattern);
            cmd.Parameters.AddWithValue("@rc", rule.ReadCapacity);
            cmd.Parameters.AddWithValue("@rr", rule.ReadRefillRatePerSecond);
            cmd.Parameters.AddWithValue("@wc", rule.WriteCapacity);
            cmd.Parameters.AddWithValue("@wr", rule.WriteRefillRatePerSecond);
            cmd.Parameters.AddWithValue("@wu", rule.WriteIsUnlimited ? 1 : 0);
            cmd.Parameters.AddWithValue("@rt", rule.ReservedTokens);
            cmd.Parameters.AddWithValue("@qt", rule.QueueTimeoutMs);
            cmd.Parameters.AddWithValue("@lu", DateTime.UtcNow.ToString("o"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static IndexRateLimitRule MapRule(IDataRecord record)
        {
            return new IndexRateLimitRule
            {
                IndexPattern = record.GetString(0),
                ReadCapacity = record.GetInt64(1),
                ReadRefillRatePerSecond = record.GetInt32(2),
                WriteCapacity = record.GetInt64(3),
                WriteRefillRatePerSecond = record.GetInt32(4),
                WriteIsUnlimited = record.GetBoolean(5),
                ReservedTokens = record.GetInt32(6),
                QueueTimeoutMs = record.GetInt32(7),
                LastUpdatedUtc = DateTime.TryParse(record.GetString(8), out var dt) ? dt : DateTime.UtcNow
            };
        }
    }
}
