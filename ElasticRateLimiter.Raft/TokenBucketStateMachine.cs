using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.RateLimiting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ElasticRateLimiter.Raft
{
    public class TokenBucketStateMachine : SimpleStateMachine
    {
        private readonly IndexPriorityTokenBucketManager _tokenBucketManager;
        private readonly ILogger<TokenBucketStateMachine> _logger;
        public TokenBucketStateMachine(string path, IndexPriorityTokenBucketManager tokenBucketManager, ILogger<TokenBucketStateMachine> logger) : base(new(path))
        {
            _tokenBucketManager = tokenBucketManager;
            _logger = logger;
        }

        protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (entry.Length == 0) return false;

            var bytes = await entry.ToByteArrayAsync();
            var payload = System.Text.Encoding.UTF8.GetString(bytes);

            try
            {
                var command = JsonSerializer.Deserialize<RateLimitLogEntry>(payload);
                if (command?.CommandType == "UpdateRule" && !string.IsNullOrWhiteSpace(command.PayloadJson))
                {
                    var rule = JsonSerializer.Deserialize<IndexRateLimitRule>(command.PayloadJson);
                    if (rule != null)
                    {
                        _tokenBucketManager.ApplyRule(rule);
                        _logger.LogInformation("Applied rule for index {IndexName}", rule.IndexPattern);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply Raft command");
            }
            return false;
        }

        protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
        {
            var rules = _tokenBucketManager.GetAllRules();
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(rules);
            await writer.WriteAsync(jsonBytes, null, token);
        }

        protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
        {
            if (!snapshotFile.Exists || snapshotFile.Length == 0)
                return;

            var jsonBytes = await File.ReadAllBytesAsync(snapshotFile.FullName, token);
            var rules = JsonSerializer.Deserialize<IEnumerable<IndexRateLimitRule>>(jsonBytes);

            if (rules != null)
            {
                foreach (var rule in rules)
                {
                    _tokenBucketManager.ApplyRule(rule);
                }
                _logger.LogInformation("Restored {RuleCount} rules from Raft snapshot", rules.Count());
            }
        }
    }
}
