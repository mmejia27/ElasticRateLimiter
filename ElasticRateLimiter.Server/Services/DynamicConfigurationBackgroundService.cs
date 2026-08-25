using System;
using System.Threading;
using System.Threading.Tasks;
using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElasticRateLimiter.Server.Services
{
    public class DynamicConfigurationBackgroundService : BackgroundService
    {
        private readonly IIndexConfigurationRepository _repository;
        private readonly IndexPriorityTokenBucketManager _tokenBucketManager;
        private readonly ILogger<DynamicConfigurationBackgroundService> _logger;
        private readonly TimeSpan _pollInterval;

        public DynamicConfigurationBackgroundService(
            IIndexConfigurationRepository repository,
            IndexPriorityTokenBucketManager tokenBucketManager,
            ILogger<DynamicConfigurationBackgroundService> logger)
        {
            _repository = repository;
            _tokenBucketManager = tokenBucketManager;
            _logger = logger;
            _pollInterval = TimeSpan.FromSeconds(60);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DynamicConfigurationBackgroundService started. Polling interval: {Interval}s", _pollInterval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var rules = await _repository.GetAllRulesAsync(stoppingToken);
                    foreach (var rule in rules)
                    {
                        _tokenBucketManager.ApplyRule(rule);
                    }
                    _logger.LogDebug("Synced {Count} index rate limit rules from database", rules.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error polling rate limit rules from database");
                }

                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
    }
}
