using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// Phase 24 Gate 3 — INV-DEDUPE-SURVIVES-RESTART, at the wiring.
///
/// <para><c>AddHbmpEvents</c> registers <c>InMemoryProcessedEventStore</c> as a fallback: a
/// <c>ConcurrentDictionary</c> that is correct for the life of one process and empty the moment it
/// restarts. Its contract is "have I ever processed this id", which a process lifetime cannot answer, and
/// an at-least-once broker redelivers after exactly the crash that emptied it — so the failure mode is a
/// second enrolment, a second dispense, a second decision, with nothing in any log to say why.</para>
///
/// <para>identity-service overrides it with a database-backed store. This asserts that any service which
/// CONSUMES events does the same, so the next consumer cannot inherit the fallback by writing no line at
/// all — an omission, which is the one thing a unit test never catches because the test that would catch
/// it is the test nobody wrote either.</para>
/// </summary>
public class ProcessedEventStoreRegistrationTests
{
    [Fact]
    public void Every_service_that_consumes_events_registers_a_durable_dedupe_store()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "services")))
        {
            var service = Path.GetFileName(dir)!;
            var sources = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList();

            // "Consumes events" = something in the service resolves or injects the dedupe store. A service
            // that merely PUBLISHES has no dedupe question to answer.
            var consumes = sources.Any(f =>
            {
                var src = File.ReadAllText(f);
                return src.Contains("GetRequiredService<IProcessedEventStore>", StringComparison.Ordinal)
                    || src.Contains("IdempotentConsumer", StringComparison.Ordinal);
            });
            if (!consumes) continue;

            var registersDurable = sources.Any(f =>
                Regex.IsMatch(File.ReadAllText(f),
                    @"Add(Scoped|Singleton|Transient)<[^>]*IProcessedEventStore\s*,\s*(?!InMemory)"));
            if (!registersDurable) offenders.Add(service);
        }

        offenders.Should().BeEmpty(
            "these services consume events but register no durable IProcessedEventStore, so they fall back " +
            "to the in-memory one and forget every processed event on restart — the redelivery that follows " +
            "a crash is then applied a second time:{0}  {1}",
            Environment.NewLine, string.Join($"{Environment.NewLine}  ", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
