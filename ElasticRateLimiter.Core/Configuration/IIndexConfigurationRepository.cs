namespace ElasticRateLimiter.Core.Configuration
{
    public interface IIndexConfigurationRepository
    {
        Task<IReadOnlyList<IndexRateLimitRule>> GetAllRulesAsync(CancellationToken cancellationToken = default);
        Task<IndexRateLimitRule?> GetRuleForIndexAsync(string indexName, CancellationToken cancellationToken = default);
        Task SaveOrUpdateRuleAsync(IndexRateLimitRule rule, CancellationToken cancellationToken = default);
    }
}
