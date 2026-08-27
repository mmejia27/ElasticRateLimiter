using DotNext;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.Diagnostics;
using ElasticRateLimiter.Core.RateLimiting;
using ElasticRateLimiter.Middleware.Extensions;
using ElasticRateLimiter.Raft;
using ElasticRateLimiter.Server.Cluster;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configure OpenTelemetry. Both exporters are opt-in: the console exporter writes a metrics dump
// to stdout every collection interval, which buries the application's own logs, and the OTLP
// exporter retries against localhost:4317 forever when no collector is running.
var useConsoleExporter = builder.Configuration.GetValue("Telemetry:Console", false);
var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];

builder.Services.AddOpenTelemetry()
    .ConfigureResource(res => res.AddService("es-rate-limiter"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(RateLimiterDiagnostics.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (useConsoleExporter) tracing.AddConsoleExporter();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(RateLimiterDiagnostics.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (useConsoleExporter) metrics.AddConsoleExporter();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    });

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

// A node with Raft:ColdStart=false owns no membership yet and must be added by the leader.
// The cold-start node bootstraps the cluster and does not announce itself.
var seeds = (builder.Configuration["Raft:Seeds"] ?? string.Empty)
    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(address => new Uri(address, UriKind.Absolute))
    .ToList();

if (seeds.Count > 0)
{
    builder.Services.AddHttpClient();
    builder.Services.AddHostedService(sp => new ClusterAutoJoinService(
        seeds,
        sp.GetRequiredService<IRaftHttpCluster>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IHostApplicationLifetime>(),
        sp.GetRequiredService<ILogger<ClusterAutoJoinService>>(),
        deadline: TimeSpan.FromMinutes(2),
        retryInterval: TimeSpan.FromSeconds(2)));
}

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

// Admits a node that announced itself via ClusterAutoJoin. Only the leader can append the
// configuration entry, so a follower answers with the leader's address for the caller to retry.
app.MapPost("/cluster/members", async (ClusterAutoJoinService.JoinRequest request, IRaftHttpCluster cluster, CancellationToken token) =>
{
    if (cluster.Leader is not { IsRemote: false })
        return Results.Json(new { leader = cluster.Leader?.EndPoint.ToString() }, statusCode: StatusCodes.Status503ServiceUnavailable);

    if (!Uri.TryCreate(request.Address, UriKind.Absolute, out var address))
        return Results.BadRequest(new { error = $"'{request.Address}' is not an absolute URI." });

    try
    {
        // False means the member was already part of the configuration - joining is idempotent.
        var added = await cluster.AddMemberAsync(address, token);
        return Results.Ok(new { address = address.ToString(), added });
    }
    // The exception is RaftCluster<TMember>.ConcurrentMembershipModificationException, and TMember
    // (RaftClusterMember) is internal to DotNext, so the closed generic cannot be named here.
    // Match on the public base type plus the name instead.
    catch (RaftProtocolException e) when (e.GetType().Name == "ConcurrentMembershipModificationException")
    {
        // Raft applies one membership change at a time. When several nodes announce together the
        // losers must simply retry, so report this as retryable rather than as a server fault.
        return Results.Json(
            new { leader = cluster.Leader?.EndPoint.ToString(), retry = true },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/cluster", (IRaftCluster cluster) => Results.Ok(new
{
    thisNodeIsLeader = cluster.Leader is { IsRemote: false },
    leader = cluster.Leader?.EndPoint.ToString(),
    term = cluster.Term,
    members = cluster.Members.Select(m => new
    {
        endPoint = m.EndPoint.ToString(),
        isLeader = m.IsLeader,
        isRemote = m.IsRemote,
        status = m.Status.ToString()
    })
}));

app.MapGet("/", () => "Running!");


app.Run();

