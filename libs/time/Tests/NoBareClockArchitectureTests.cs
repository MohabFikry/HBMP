using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Time.Tests;

/// <summary>
/// Phase 18.A3 — architecture gate: production code may not read the wall clock directly.
///
/// A bare <c>DateTimeOffset.UtcNow</c> / <c>DateTime.UtcNow</c> is untestable (no boundary test can pin
/// it) and, when it feeds a DATE decision, silently wrong for the two-to-three hours every evening when
/// Cairo has rolled over and UTC has not. Production code takes an injected <see cref="TimeProvider"/>
/// for instants and <see cref="IBusinessCalendar"/> for dates. <c>DateTime.Now</c> — machine-local time —
/// is banned outright with no exceptions.
///
/// The allowlist below is deliberately small and each entry carries its reason. It is not a way to opt
/// out: adding a file to it is a reviewable change, and the test fails the moment a new bare clock read
/// appears anywhere else.
/// </summary>
public class NoBareClockArchitectureTests
{
    /// <summary>Files that may read the clock directly, with the reason. Repo-relative, forward slashes.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["libs/time/BusinessCalendar.cs"] =
            "the calendar itself — it is the wrapper everything else uses",
        ["libs/events/OutboxMessage.cs"] =
            "property initialiser default on a POCO; the real value is stamped by OutboxBase",
        ["libs/events/Outbox.cs"] =
            "transport-level envelope timestamp, not a business date",
        ["libs/events/InMemoryOutbox.cs"] =
            "test/dev double for the durable outbox; transport-level only",
        ["services/audit/Infrastructure/MinioWormStore.cs"] =
            "object-lock retention is computed against the storage backend's own clock",
        ["services/eligibility/Api/ConsumerHealth.cs"] =
            "liveness heartbeat ticks; never read as a business date",
        ["services/hello/Api/Program.cs"] =
            "reference/scaffold service, not a business path (slated for deletion in 18.E2)",
    };

    private static readonly Regex BareUtcNow =
        new(@"\b(DateTimeOffset|DateTime)\s*\.\s*UtcNow\b", RegexOptions.Compiled);
    private static readonly Regex MachineLocalNow =
        new(@"\bDateTime\s*\.\s*Now\b", RegexOptions.Compiled);

    [Fact]
    public void No_production_file_reads_the_wall_clock_directly()
    {
        var offenders = ProductionFiles()
            .Where(f => BareUtcNow.IsMatch(File.ReadAllText(f.Absolute)))
            .Select(f => f.Relative)
            .Where(rel => !Allowed.ContainsKey(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "production code must take an injected TimeProvider for instants and IBusinessCalendar.Today() " +
            "for dates — a bare UtcNow is untestable and gives the wrong DATE every Cairo evening. " +
            "Offending files:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_code_anywhere_reads_machine_local_time()
    {
        // DateTime.Now depends on the host's time zone, so the same code gives different answers on a
        // developer laptop, a CI runner and a clinic PC. There is no legitimate use.
        var offenders = ProductionFiles()
            .Where(f => MachineLocalNow.IsMatch(File.ReadAllText(f.Absolute)))
            .Select(f => f.Relative)
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty("DateTime.Now is machine-local and never correct here. Offending files:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_allowlist_has_no_stale_entries()
    {
        // An allowlist that outlives its reason quietly widens the gate. Every entry must still be a real
        // file that still reads the clock.
        var files = ProductionFiles().ToDictionary(f => f.Relative, f => f.Absolute, StringComparer.Ordinal);

        foreach (var (relative, reason) in Allowed)
        {
            files.Should().ContainKey(relative, "allowlisted file must exist ({0})", reason);
            BareUtcNow.IsMatch(File.ReadAllText(files[relative]))
                .Should().BeTrue("allowlist entry {0} no longer reads the clock — remove it", relative);
        }
    }

    // ── discovery ─────────────────────────────────────────────────────────────────────────────────

    private sealed record SourceFile(string Relative, string Absolute);

    private static IEnumerable<SourceFile> ProductionFiles()
    {
        var root = RepoRoot();
        foreach (var area in new[] { "services", "libs" })
        {
            var dir = Path.Combine(root, area);
            if (!Directory.Exists(dir)) continue;
            foreach (var abs in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) ||
                    rel.Contains("/obj/", StringComparison.Ordinal) ||
                    rel.Contains("/Tests/", StringComparison.Ordinal) ||
                    rel.Contains("/Migrations/", StringComparison.Ordinal))
                    continue;
                yield return new SourceFile(rel, abs);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (HbmpPlatform.sln) not found");
    }
}
