using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// A durable outbox without a relay is a postbox with no postman.
///
/// <para><c>AddHbmpDurableOutbox&lt;T&gt;</c> stages every domain event — and, because
/// <c>AddHbmpEvents</c> reroutes the audit client through the same outbox, every audit event — into the
/// service's own <c>outbox</c> table inside the business transaction. <c>AddHbmpOutboxRelay</c> is what
/// drains it onto the broker. Registering the first without the second is silent: writes succeed, the
/// table grows, and nothing is ever delivered. Nothing fails, so nothing alarms.</para>
///
/// <para>document-service shipped exactly that omission and kept it long enough for every upload and
/// download of an identified-person extract to leave no trail outside its own database. The bug is an
/// absent line, which is the one defect no unit test of present code can reach — so it is asserted here,
/// over the wiring itself.</para>
/// </summary>
public class OutboxRelayRegistrationTests
{
    [Fact]
    public void Every_service_that_stages_a_durable_outbox_also_registers_the_relay()
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
                .Select(File.ReadAllText)
                .ToList();

            var stages = sources.Any(s => s.Contains("AddHbmpDurableOutbox", StringComparison.Ordinal));
            if (!stages) continue;

            var relays = sources.Any(s => s.Contains("AddHbmpOutboxRelay", StringComparison.Ordinal));
            if (!relays) offenders.Add(service);
        }

        offenders.Should().BeEmpty(
            "these services stage events into a durable outbox but register no relay to drain it, so every " +
            "domain and audit event they raise is written and never delivered:{0}  {1}",
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
