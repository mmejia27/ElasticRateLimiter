using System;
using ElasticRateLimiter.Core.Cost;
using ElasticRateLimiter.Core.Index;
using ElasticRateLimiter.Core.RateLimiting;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElasticRateLimiter.Middleware.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddElasticsearchRateLimiter(
            this IServiceCollection services,
            Action<ElasticsearchRateLimiterOptions>? configureOptions = null)
        {
            var options = new ElasticsearchRateLimiterOptions();
            configureOptions?.Invoke(options);

            services.Configure<ElasticsearchRateLimiterOptions>(opt =>
            {
                opt.ElasticsearchTargetUrl = options.ElasticsearchTargetUrl;
                opt.EnableReverseProxy = options.EnableReverseProxy;
                opt.DefaultQueueTimeoutMs = options.DefaultQueueTimeoutMs;
            });

            services.AddSingleton<IElasticsearchQueryCostEstimator, ElasticsearchQueryCostEstimator>();
            services.AddSingleton<IElasticsearchIndexExtractor, ElasticsearchIndexExtractor>();
            services.AddSingleton<IndexPriorityTokenBucketManager>(sp => 
            {
                int GetClusterSize()
                {
                    var cluster = sp.GetService<IRaftCluster>();
                    return cluster != null && cluster.Members.Count > 0 ? cluster.Members.Count : 1;
                }
                return new IndexPriorityTokenBucketManager(GetClusterSize);
            });
            services.AddHttpClient("ElasticsearchProxy");

            return services;
        }

        public static IApplicationBuilder UseElasticsearchRateLimiter(this IApplicationBuilder app)
        {
            return app.UseMiddleware<ElasticsearchRateLimiterMiddleware>();
        }
    }
}
