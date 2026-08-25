using DotNext;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.Diagnostics;
using ElasticRateLimiter.Core.RateLimiting;
using ElasticRateLimiter.Middleware.Extensions;
using ElasticRateLimiter.Raft;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configure OpenTelemetry
builder.Services.AddOpenTelemetry()
    .ConfigureResource(res => res.AddService("es-rate-limiter"))
    .WithTracing(tracing => tracing
        .AddSource(RateLimiterDiagnostics.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(RateLimiterDiagnostics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter()
        .AddOtlpExporter());

var statePath = builder.Configuration["Raft:StateDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "raft-state");

builder.Services.AddSingleton<IndexPriorityTokenBucketManager>(sp =>
{
    return new IndexPriorityTokenBucketManager(() =>
    {
        var cluster = sp.GetService<IRaftCluster>();
        return cluster?.Members.Count ?? 1;
    });
});

builder.Services.AddSingleton<TokenBucketStateMachine>(sp =>
{
    var manager = sp.GetRequiredService<IndexPriorityTokenBucketManager>();
    var logger = sp.GetRequiredService<ILogger<TokenBucketStateMachine>>();
    return new TokenBucketStateMachine(statePath, manager, logger);
});
/* Cannot register the state machine due to it not implementing IPersistentState and documentation is sparse
builder.Services.AddSingleton<IPersistentState>(sp => sp.GetRequiredService<TokenBucketStateMachine>());
builder.Host.JoinCluster();
*/
var app = builder.Build();

/* Part of the wiring for DotNext cluster, not working due to the above
app.UseConsensusProtocolHandler();

await app.RestoreStateAsync<TokenBucketStateMachine>();

app.MapGet("/rules", (IndexPriorityTokenBucketManager manager) =>
{
    return Results.Ok(manager.GetAllRules());
});

app.MapPost("/rules", async (IndexRateLimitRule rule, IndexPriorityTokenBucketManager tbManager, IRaftCluster cluster) =>
{
    tbManager.ApplyRule(rule);

    // Replicate to cluster
    var entry = RateLimitLogEntry.CreateUpdateRule(rule);

    return Results.Ok(new { message = "Rule saved and applied successfully", rule });
});
*/
app.MapGet("/", () => "Running!");


app.Run();

