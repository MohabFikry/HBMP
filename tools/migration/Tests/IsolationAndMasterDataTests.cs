using FluentAssertions;
using Mersal.Migration.Core;
using Mersal.Migration.Streams;

namespace Mersal.Migration.Tests;

public sealed class IsolationAndMasterDataTests
{
    [Fact]
    public async Task Provider_stream_loads_users_and_isolation_passes_when_scoped()
    {
        var sink = new InMemorySink();
        var audit = new CapturingAudit();
        var rows = new IReadOnlyDictionary<string, string?>[]
        {
            new Dictionary<string, string?> { ["source_id"] = "1", ["provider_id"] = "prov-A", ["user_id"] = "u1", ["username"] = "labA", ["role"] = "lab_tech" },
            new Dictionary<string, string?> { ["source_id"] = "2", ["provider_id"] = "prov-B", ["user_id"] = "u2", ["username"] = "labB", ["role"] = "lab_tech" },
        };
        var batch = MigrationBatch.Start(DefaultConfigs.Providers(), "staging", DateTimeOffset.UtcNow, masked: true);

        var (recon, users) = await new ProviderStream(sink, audit, TimeProvider.System).RunAsync(batch, DefaultConfigs.Providers(), rows);
        recon.Inserted.Should().Be(2);
        recon.Balances.Should().BeTrue();

        // Correctly-scoped world: each user only ever sees their own provider.
        var ok = ProviderIsolationVerifier.Verify(users, u => new[] { u.ProviderId });
        ok.Isolated.Should().BeTrue();
    }

    [Fact]
    public void Isolation_verifier_flags_cross_provider_leakage()
    {
        var users = new[]
        {
            new ProviderUserRow("1", "prov-A", "u1", "labA", "lab_tech"),
            new ProviderUserRow("2", "prov-B", "u2", "labB", "lab_tech"),
        };
        // Broken world: user u1 can see prov-B's rows too.
        var result = ProviderIsolationVerifier.Verify(users, u => u.UserId == "u1" ? ["prov-A", "prov-B"] : [u.ProviderId]);

        result.Isolated.Should().BeFalse();
        result.Findings.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new IsolationFinding("u1", "prov-A", "prov-B"));
    }

    [Fact]
    public void Master_data_reconcile_passes_when_counts_and_versions_match()
    {
        var checks = new[]
        {
            new DatasetCheck("icd10", 74000, 74000, "2024", "2024"),
            new DatasetCheck("atc-drugs", 6300, 6300, "2024.1", "2024.1"),
        };
        var recon = MasterDataStream.Reconcile(Guid.NewGuid(), checks);
        recon.Rejected.Should().Be(0);
        recon.Balances.Should().BeTrue();
    }

    [Fact]
    public void Master_data_reconcile_flags_count_and_version_drift()
    {
        var checks = new[]
        {
            new DatasetCheck("icd10", 74000, 73990, "2024", "2024"),      // count drift
            new DatasetCheck("atc-drugs", 6300, 6300, "2024.1", "2023.9"), // version drift
        };
        var recon = MasterDataStream.Reconcile(Guid.NewGuid(), checks);
        recon.Rejected.Should().Be(2);
        recon.Exceptions.Should().Contain(e => e.Reason.Contains("count drift"));
        recon.Exceptions.Should().Contain(e => e.Reason.Contains("version drift"));
    }
}
