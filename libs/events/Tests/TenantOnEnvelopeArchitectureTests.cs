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
        // Guards the guard: a regex that silently matches nothing would make the test above vacuously green.
        var found = SourceFiles()
            .Sum(f => TenantBoundQueues.Sum(q => PublishSites(File.ReadAllText(f), q.Queue).Count));
        found.Should().BeGreaterThan(5, "the platform has many publishers on these four queues");
    }

    private static MatchCollection PublishSites(string source, string queue) =>
        Regex.Matches(source, $@"EnqueueAsync\(\s*""[^""]+""\s*,\s*""{Regex.Escape(queue)}""\s*,");

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
