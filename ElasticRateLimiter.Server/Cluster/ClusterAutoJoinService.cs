using System.Net;
using System.Net.Http.Json;

using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;

namespace ElasticRateLimiter.Server.Cluster;

/// <summary>
/// Lets a node join an existing cluster at runtime instead of every node being configured with the
/// full member list up front. A node started with Raft:ColdStart=false announces itself to one of
/// the Raft:Seeds addresses; the seed forwards to the leader, which appends a configuration entry
/// that replicates the new membership to everyone.
/// </summary>
/// <remarks>
/// This deliberately does not use DotNext's <c>ClusterMemberAnnouncer</c> hook. That hook is
/// invoked while the Raft cluster is starting, which happens *before* Kestrel begins listening, so
/// the leader admits the node and then cannot reach it - the connection is refused and the
/// membership change fails. Announcing from a hosted service that waits for ApplicationStarted
/// means the node is already serving by the time the leader tries to replicate to it.
/// </remarks>
public sealed class ClusterAutoJoinService(
    IReadOnlyList<Uri> seeds,
    IRaftHttpCluster cluster,
    IHttpClientFactory httpClientFactory,
    IHostApplicationLifetime lifetime,
    ILogger<ClusterAutoJoinService> logger,
    TimeSpan deadline,
    TimeSpan retryInterval) : BackgroundService
{
    /// <summary>Body of a join request: the address the joining node advertises to its peers.</summary>
    public sealed record JoinRequest(string Address);

    private sealed record JoinResponse(string? Leader, bool Retry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForApplicationStartedAsync(stoppingToken);

        // A node that has joined before recovers its peers from the persisted configuration during
        // startup and rejoins consensus on its own. Announcing again would be harmless but pointless,
        // and would fail noisily for two minutes whenever a seed happens to be down - so only a node
        // that came up with no membership at all needs to announce itself.
        // IRaftHttpCluster inherits Members from both IRaftCluster and IMessageBus, so pick one.
        var knownMembers = ((IRaftCluster)cluster).Members.Count;
        if (knownMembers > 0)
        {
            logger.LogInformation("Already a member of a {Count}-node cluster; skipping announcement", knownMembers);
            return;
        }

        var self = cluster.LocalMemberAddress.ToString();

        // Seeds are tried in order. A seed that knows the leader hands back its address, and we try
        // that first on the next pass, because only the leader can change the configuration.
        var targets = new List<Uri>(seeds);
        var started = TimeProvider.System.GetTimestamp();

        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            foreach (var target in targets.ToArray())
            {
                if (await TryJoinAsync(target, self, targets, stoppingToken))
                {
                    logger.LogInformation("Joined the cluster as {Address} via {Seed}", self, target);
                    return;
                }
            }

            if (TimeProvider.System.GetElapsedTime(started) > deadline)
            {
                logger.LogError(
                    "Could not join the cluster as {Address} within {Deadline}; tried {Targets}",
                    self, deadline, string.Join(", ", targets));
                return;
            }

            if (attempt % 5 is 0)
                logger.LogInformation("Still trying to join the cluster as {Address} (attempt {Attempt})", self, attempt);

            await Task.Delay(retryInterval, stoppingToken);
        }
    }

    private async Task<bool> TryJoinAsync(Uri target, string self, List<Uri> targets, CancellationToken token)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.PostAsJsonAsync(
                new Uri(target, "/cluster/members"), new JoinRequest(self), token);

            if (response.IsSuccessStatusCode)
                return true;

            // Not the leader, or a membership change is already in flight. Both are retryable, and
            // the response may name the leader - prefer it next time.
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable)
            {
                var hint = await response.Content.ReadFromJsonAsync<JoinResponse>(token);
                if (Uri.TryCreate(hint?.Leader, UriKind.Absolute, out var leader) && !targets.Contains(leader))
                {
                    targets.Insert(0, leader);
                    logger.LogDebug("Seed {Seed} redirected us to leader {Leader}", target, leader);
                }
            }
        }
        catch (Exception e)
            when (e is HttpRequestException or TaskCanceledException or NotSupportedException && !token.IsCancellationRequested)
        {
            // Seed not up yet, or no leader elected yet. Expected while a cluster is starting.
        }

        return false;
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
            return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
    }
}
