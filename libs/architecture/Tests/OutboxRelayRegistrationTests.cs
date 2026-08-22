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

    /// <summary>
    /// And the table both of them talk to exists.
    /// </summary>
    /// <remarks>
    /// <para>The test above checks registration against registration. inventory-service had BOTH — it staged
    /// a durable outbox and it registered the relay — and no migration creating <c>inventory.outbox_message</c>.
    /// Nineteen services carry a <c>9000_outbox.sql</c>; that one shipped without it.</para>
    ///
    /// <para>The consequence was not a lost event. <c>AddHbmpEvents</c> reroutes the AUDIT client through the
    /// same outbox, so an audited READ writes here too — and with no table every request to the service ended
    /// in <c>42P01: relation … does not exist</c> and answered 500, while the relay logged the same failure
    /// once a second forever. The screen reported "the service couldn't complete this request" for what was a
    /// missing DDL file.</para>
    ///
    /// <para>Read from the MIGRATIONS rather than from a live database, so it fails in the ordinary test run
    /// on a developer's machine rather than after a deploy.</para>
    /// </remarks>
    [Fact]
    public void Every_service_that_stages_a_durable_outbox_has_a_migration_creating_the_table()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var checkedServices = 0;

        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "services")))
        {
            var service = Path.GetFileName(dir)!;
            var stages = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Any(f => File.ReadAllText(f).Contains("AddHbmpDurableOutbox", StringComparison.Ordinal));
            if (!stages) continue;

            checkedServices++;
            var migrations = Path.Combine(dir, "Infrastructure", "Migrations");
            var creates = Directory.Exists(migrations)
                && Directory.EnumerateFiles(migrations, "*.sql")
                    .Any(f => File.ReadAllText(f).Contains("outbox_message", StringComparison.OrdinalIgnoreCase));
            if (!creates) offenders.Add(service);
        }

        checkedServices.Should().BeGreaterThan(10,
            "a scan finding almost no outbox services is reading the wrong tree and would pass on an empty set");
        offenders.Should().BeEmpty(
            "these services stage events into a durable outbox that has no table — every request that raises " +
            "a domain OR audit event fails with 42P01 and the relay fails on every pass:{0}  {1}",
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
