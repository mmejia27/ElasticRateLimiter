using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using ElasticRateLimiter.Core.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ElasticRateLimiter.Raft
{
    public class TokenBucketStateMachine : SimpleStateMachine
    {
        private readonly ILogger<TokenBucketStateMachine> _logger;
        public TokenBucketStateMachine(string path, ILogger<TokenBucketStateMachine> logger) : base(new(path))
        {
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
                        // TODO: Apply rule here
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

        protected override ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        protected override ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
        {
            throw new NotImplementedException();
        }
    }
}
