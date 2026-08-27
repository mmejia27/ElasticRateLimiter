using DotNext.Net.Cluster.Consensus.Raft;

using ElasticRateLimiter.Core.Configuration;
using ElasticRateLimiter.Core.RateLimiting;
using ElasticRateLimiter.Raft;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ElasticRateLimiter.Server.Pages;

/// <summary>
/// Demo admin page: lists every replicated rule with a per-rule form. No authentication - do not
/// expose this beyond a demo.
/// </summary>
public sealed class AdminModel(
    IndexPriorityTokenBucketManager manager,
    IRaftCluster cluster,
    IHttpClientFactory httpClientFactory) : PageModel
{
    public IReadOnlyList<IndexRateLimitRule> Rules { get; private set; } = [];

    public string? LeaderAddress => cluster.Leader?.EndPoint.ToString();

    public bool ThisNodeIsLeader => cluster.Leader is { IsRemote: false };

    [BindProperty]
    public RuleInput Input { get; set; } = new();

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public bool IsError { get; set; }

    public void OnGet()
    {
        LoadRules();

        // Seed the "add a rule" form from a fresh rule so its defaults track the model rather than
        // being restated here.
        Input = RuleInput.Defaults();
    }

    /// <summary>Saves an edit to a rule that already exists.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Input.IndexPattern))
            return Failed("A rule needs an index pattern.");

        var rule = Input.ToRule();
        var error = await SaveAsync(rule, cancellationToken);

        return error is not null
            ? Failed(error)
            : Succeeded($"Saved '{rule.IndexPattern}' and replicated it to the cluster.");
    }

    /// <summary>Creates a rule for a pattern that has none yet.</summary>
    public async Task<IActionResult> OnPostAddAsync(CancellationToken cancellationToken)
    {
        var pattern = Input.IndexPattern?.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
            return Failed("A new rule needs an index pattern, for example 'logs-*'.");

        // Saving is an upsert, so without this an existing pattern typed here would silently
        // overwrite the rule shown above instead of adding anything.
        if (manager.GetAllRules().Any(r => string.Equals(r.IndexPattern, pattern, StringComparison.Ordinal)))
            return Failed($"A rule for '{pattern}' already exists - edit it above.");

        Input.IndexPattern = pattern;
        var rule = Input.ToRule();
        var error = await SaveAsync(rule, cancellationToken);

        return error is not null
            ? Failed(error)
            : Succeeded($"Added '{pattern}' and replicated it to the cluster.");
    }

    /// <summary>Replicates the rule, or forwards it to the leader. Returns null on success.</summary>
    private async Task<string?> SaveAsync(IndexRateLimitRule rule, CancellationToken cancellationToken)
    {
        try
        {
            if (ThisNodeIsLeader)
            {
                var entry = RateLimitLogEntry.CreateUpdateRule(rule);
                await cluster.ReplicateAsync(entry.ToUtf8Bytes(), token: cancellationToken);
            }
            else if (cluster.Leader?.EndPoint is UriEndPoint leader)
            {
                // Only the leader can append to the log. Forward rather than telling the operator to
                // go find it: the leader's address is internal to the cluster and unreachable from
                // the browser.
                using var client = httpClientFactory.CreateClient();
                using var response = await client.PostAsJsonAsync(new Uri(leader.Uri, "/rules"), rule, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            else
            {
                return "No leader elected yet; try again shortly.";
            }
        }
        catch (Exception e)
        {
            return $"Could not save '{rule.IndexPattern}': {e.GetBaseException().Message}";
        }

        return null;
    }

    private IActionResult Succeeded(string message)
    {
        Message = message;
        IsError = false;

        // Redirect after POST so a refresh does not resubmit the form.
        return RedirectToPage();
    }

    private IActionResult Failed(string message)
    {
        Message = message;
        IsError = true;
        return RedirectToPage();
    }

    private void LoadRules()
        => Rules = manager.GetAllRules().OrderBy(r => r.IndexPattern, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Element ids must be unique across the whole page, not just within one form, or clicking a
    /// label focuses the matching field of whichever rule rendered first. Index patterns contain
    /// characters like '*' and '.', so reduce them to something usable as an id prefix.
    /// </summary>
    public static string IdScope(string indexPattern)
        => string.Create(indexPattern.Length, indexPattern, static (span, pattern) =>
        {
            for (var i = 0; i < pattern.Length; i++)
                span[i] = char.IsAsciiLetterOrDigit(pattern[i]) ? pattern[i] : '_';
        });

    /// <summary>Form fields for one rule. Bound from whichever form on the page was submitted.</summary>
    public sealed class RuleInput
    {
        /// <summary>Starting values for the "add a rule" form, taken from the rule model's own defaults.</summary>
        public static RuleInput Defaults()
        {
            var template = new IndexRateLimitRule();
            return new RuleInput
            {
                IndexPattern = string.Empty,
                ReadCapacity = template.ReadCapacity,
                ReadRefillRatePerSecond = template.ReadRefillRatePerSecond,
                ReservedTokens = template.ReservedTokens,
                QueueTimeoutMs = template.QueueTimeoutMs,
                WriteCapacity = template.WriteCapacity,
                WriteRefillRatePerSecond = template.WriteRefillRatePerSecond,
                WriteIsUnlimited = template.WriteIsUnlimited,
            };
        }

        public string IndexPattern { get; set; } = string.Empty;
        public long ReadCapacity { get; set; }
        public int ReadRefillRatePerSecond { get; set; }
        public int ReservedTokens { get; set; }
        public int QueueTimeoutMs { get; set; }
        public long WriteCapacity { get; set; }
        public int WriteRefillRatePerSecond { get; set; }
        public bool WriteIsUnlimited { get; set; }

        public IndexRateLimitRule ToRule() => new()
        {
            IndexPattern = IndexPattern,
            ReadCapacity = ReadCapacity,
            ReadRefillRatePerSecond = ReadRefillRatePerSecond,
            ReservedTokens = ReservedTokens,
            QueueTimeoutMs = QueueTimeoutMs,
            WriteCapacity = WriteCapacity,
            WriteRefillRatePerSecond = WriteRefillRatePerSecond,
            WriteIsUnlimited = WriteIsUnlimited,
            LastUpdatedUtc = DateTime.UtcNow,
        };
    }
}
