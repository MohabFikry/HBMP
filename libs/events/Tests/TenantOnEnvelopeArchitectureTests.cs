using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Events.Tests;

/// <summary>
/// Phase 18.B2 — a source scan proving every domain event published onto a queue that an RLS-binding
/// background consumer reads carries <c>tenantId</c> on its envelope.
///
/// This exists because the failure mode is silent in both directions. Before 18.B2, eligibility-service
/// stamped a hardcoded tenant, so a missing <c>tenantId</c> cost nothing and nobody noticed the publishers
/// had never emitted one. After 18.B2 the consumer dead-letters an untenanted message — which is correct,
/// but it means a publisher that quietly drops the field stops the projection instead of corrupting it, and
/// stops it in a background loop where the only symptom is a stale read model. A compile-time-ish check on
/// the publish sites catches that in the pull request rather than in the dead-letter queue.
/// </summary>
public class TenantOnEnvelopeArchitectureTests
{
    /// <summary>Queues whose consumers bind the RLS GUC from the envelope. Add a row when a new consumer
    /// does the same; the point of the pairing is that the requirement is visible from the publisher side.</summary>
    private static readonly (string Queue, string Consumer)[] TenantBoundQueues =
    [
        ("patient.events",  "eligibility-service EventConsumer"),
        ("policy.events",   "eligibility-service EventConsumer"),
        ("orders.events",   "policy-service BenefitConsumptionConsumer"),
        ("pharmacy.events", "policy-service BenefitConsumptionConsumer"),
        // ADR-0031 — these three are ALSO mirrored to emr's care-episode consumer (CareFeed), which binds its
        // RLS session from the envelope exactly as policy's does. approvals.events is listed because that
        // mirror is its FIRST tenant-binding consumer: nothing read it before, so nothing had ever noticed
        // that its publishers omitted the tenant.
        ("approvals.events", "emr-service CareEpisodeConsumer (via the CareFeed mirror)"),

        // The reporting read model. `ProjectionConsumer` mirrors these streams onto its own queue and binds
        // RLS from the envelope, dead-lettering anything it cannot attribute — so these are tenant-bound
        // queues too, and were never listed here.
        //
        // WHAT THAT COST. All four of emr's publishers on `ProjectionFeed` — ApptBooked, ApptCheckedIn,
        // ApptNoShow, EncounterStarted — omitted the tenant, so every one of them was nacked on arrival. The
        // Clinic Workload report and the no-show rate had no facts at all, and a clinic with no visits looks
        // exactly like a clinic whose visits were dropped. Nothing failed; the dashboards rendered zero.
        ("emr.events", "reporting-service ProjectionConsumer (via the ProjectionFeed mirror)"),
        ("claims.events", "reporting-service ProjectionConsumer (via the ProjectionFeed mirror)"),
    ];

    [Fact]
    public void Every_event_published_to_a_tenant_bound_queue_carries_its_tenant()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var source = File.ReadAllText(file);
            foreach (var (queue, consumer) in TenantBoundQueues)
            {
                foreach (Match publish in PublishSites(source, queue))
                {
                    var payload = PayloadOf(source, publish.Index + publish.Length);
                    if (payload.Contains("tenantId", StringComparison.Ordinal) ||
                        payload.Contains("tenant_id", StringComparison.Ordinal)) continue;

                    var line = source[..publish.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Relative(file)}:{line} publishes to {queue} without tenantId — {consumer} " +
                                  "binds its RLS session from the envelope and will dead-letter this message");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a tenant-bound consumer cannot attribute an envelope that omits its tenant:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_scan_actually_finds_the_publish_sites_it_claims_to_check()
    {
        // Guards the guard: a scanner that silently matches nothing would make the test above vacuously green.
        // PER QUEUE, not in total — a summed count stays comfortably above a threshold while one queue's
        // publishers have all become invisible, which is the failure that actually happens.
        foreach (var (queue, consumer) in TenantBoundQueues)
        {
            var found = SourceFiles().Sum(f => PublishSites(File.ReadAllText(f), queue).Count);
            found.Should().BeGreaterThan(0,
                "nothing was found publishing to {0}, so the check for {1} is asserting nothing — either the "
                + "queue name changed or the scanner can no longer read its call sites", queue, consumer);
        }
    }

    /// <summary>
    /// Every <c>EnqueueAsync</c> call site publishing to <paramref name="queue"/>.
    /// </summary>
    /// <remarks>
    /// <para>Anchored on the QUEUE argument and then walked backwards to the call, rather than matched
    /// forwards from <c>EnqueueAsync(</c> across the event-type argument. The forward pattern required that
    /// argument to be a bare string literal, and one publisher builds it with a ternary —
    /// <c>req.Decision == AuthDecision.Approved ? "AuthApproved" : "AuthPartiallyApproved"</c> — so
    /// approvals-service's break-glass path was invisible to this scan for as long as it has existed, while
    /// `approvals.events` sat in the register above looking checked. It published no tenant, and every
    /// manual and emergency approval was dead-lettered by two consumers.</para>
    /// <para>A statement boundary (<c>;</c>) between the queue name and the call stops a string that merely
    /// mentions the queue — a log message, a configuration default — from being read as a publish.</para>
    /// </remarks>
    private static IReadOnlyList<Match> PublishSites(string source, string queue)
    {
        var sites = new List<Match>();
        foreach (Match at in Regex.Matches(source, $@"""{Regex.Escape(queue)}""\s*,"))
        {
            var window = source[Math.Max(0, at.Index - 400)..at.Index];
            var call = window.LastIndexOf("EnqueueAsync(", StringComparison.Ordinal);
            if (call < 0) continue;
            if (window[call..].Contains(';', StringComparison.Ordinal)) continue;
            sites.Add(at);
        }
        return sites;
    }

    /// <summary>The anonymous-object payload that follows the queue argument, read to its balanced close so a
    /// nested <c>new { … }</c> (limits, identifiers) is included rather than truncating the search early.</summary>
    private static string PayloadOf(string source, int start)
    {
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return source[start..(i + 1)];
                    break;
                // A payload with no braces at all (a bare variable or a record) ends at the call's close paren.
                case ')' when depth == 0: return source[start..i];
            }
        }
        return source[start..];
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "services"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
