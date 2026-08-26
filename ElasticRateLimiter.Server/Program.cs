using DotNext;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
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


var walOptions = new WriteAheadLog.Options
{
    Location = statePath,
};

builder.Services.AddSingleton(new TokenBucketStateMachineOptions(Path.Combine(statePath, "snapshot")));
builder.Services.UseStateMachine<TokenBucketStateMachine>(walOptions);
builder.Services.UsePersistentConfigurationStorage(Path.Combine(statePath, "members"));

builder.Host.JoinCluster("raft");

var app = builder.Build();

await app.RestoreStateAsync<TokenBucketStateMachine>();

app.UseConsensusProtocolHandler();

app.MapGet("/rules", (IndexPriorityTokenBucketManager manager) =>
{
    return Results.Ok(manager.GetAllRules());
});

app.MapPost("/rules", async (IndexRateLimitRule rule, IRaftCluster cluster, CancellationToken token) =>
{
    // Only the leader may append to the Raft log.
    if (cluster.Leader is not { IsRemote: false })
    {
        return Results.Problem(
            detail: $"This node is not the Raft leader. Current leader: {cluster.Leader?.EndPoint.ToString() ?? "unknown"}.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var entry = RateLimitLogEntry.CreateUpdateRule(rule);
    await cluster.ReplicateAsync(entry.ToUtf8Bytes(), token: token);

    return Results.Ok(new { message = "Rule replicated and applied successfully", rule });
});

app.MapGet("/", () => "Running!");


app.Run();

