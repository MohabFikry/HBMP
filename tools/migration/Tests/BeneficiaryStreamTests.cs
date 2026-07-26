using FluentAssertions;
using Mersal.Migration.Core;
using Mersal.Migration.Streams;

namespace Mersal.Migration.Tests;

public sealed class BeneficiaryStreamTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private static Dictionary<string, string?> Row(string sourceId, string name, string nationalId, string? dob = null) => new()
    {
        ["source_id"] = sourceId, ["full_name"] = name, ["national_id"] = nationalId, ["birth_date"] = dob,
    };

    private static MigrationBatch NewBatch()
        => MigrationBatch.Start(DefaultConfigs.Beneficiaries(), "staging", DateTimeOffset.UtcNow, masked: true);

    [Fact]
    public async Task Loads_new_beneficiaries_with_provenance_audit_and_balanced_reconciliation()
    {
        var sink = new InMemorySink();
        var audit = new CapturingAudit();
        var rows = new IReadOnlyDictionary<string, string?>[]
        {
            Row("a", "Layla Hassan", "29001010123456", "1990-01-01"),
            Row("b", "Omar Farouk", "29505050123456", "1995-05-05"),
        };

        var (recon, dedupe) = await new BeneficiaryStream(sink, audit, Clock).RunAsync(NewBatch(), DefaultConfigs.Beneficiaries(), rows, []);

        recon.Inserted.Should().Be(2);
        recon.Balances.Should().BeTrue();
        dedupe.NoMatch.Should().HaveCount(2);
        (await sink.ActiveRowsAsync(BeneficiaryStream.StreamName)).Should().HaveCount(2)
            .And.OnlyContain(r => r.Provenance.SourceSystem == "legacy-beneficiary-registry");
        audit.Events.Should().HaveCount(2).And.OnlyContain(e => e.EntityType == "beneficiary");
    }

    [Fact]
    public async Task Rerunning_a_batch_is_idempotent_no_duplicates()
    {
        var sink = new InMemorySink();
        var audit = new CapturingAudit();
        var rows = new IReadOnlyDictionary<string, string?>[] { Row("a", "Layla Hassan", "29001010123456", "1990-01-01") };

        await new BeneficiaryStream(sink, audit, Clock).RunAsync(NewBatch(), DefaultConfigs.Beneficiaries(), rows, []);
        var (recon2, _) = await new BeneficiaryStream(sink, audit, Clock).RunAsync(NewBatch(), DefaultConfigs.Beneficiaries(), rows, []);

        recon2.Inserted.Should().Be(0);
        recon2.Updated.Should().Be(1);
        (await sink.ActiveRowsAsync(BeneficiaryStream.StreamName)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Rollback_by_batch_reverts_only_that_batch()
    {
        var sink = new InMemorySink();
        var audit = new CapturingAudit();
        var stream = new BeneficiaryStream(sink, audit, Clock);

        var batch1 = NewBatch();
        await stream.RunAsync(batch1, DefaultConfigs.Beneficiaries(),
            [Row("a", "Layla Hassan", "29001010123456", "1990-01-01")], []);
        var batch2 = NewBatch();
        await stream.RunAsync(batch2, DefaultConfigs.Beneficiaries(),
            [Row("b", "Omar Farouk", "29505050123456", "1995-05-05")], []);

        var reverted = await sink.RollbackBatchAsync(batch2.BatchId);

        reverted.Should().Be(1);
        var remaining = await sink.ActiveRowsAsync(BeneficiaryStream.StreamName);
        remaining.Should().ContainSingle().Which.Provenance.BatchId.Should().Be(batch1.BatchId);
    }

    [Fact]
    public async Task Review_pairs_are_held_not_loaded_and_reconciliation_still_balances()
    {
        var sink = new InMemorySink();
        var audit = new CapturingAudit();
        // Same strong name, different DOB, different id → dedupe routes to review; row is HELD.
        var existing = new[] { new KnownPerson("p1", ["NationalId:29001010123456"], "Mohamed Ali Ibrahim", new DateOnly(1985, 6, 12)) };
        var rows = new IReadOnlyDictionary<string, string?>[]
        {
            Row("dup", "Mohammed Ali Ibrahim", "30001010123456", "1991-02-02"),
        };

        var (recon, dedupe) = await new BeneficiaryStream(sink, audit, Clock)
            .RunAsync(NewBatch(), DefaultConfigs.Beneficiaries(), rows, existing);

        dedupe.QueuedForReview.Should().ContainSingle();
        recon.Held.Should().Be(1);
        recon.Loaded.Should().Be(0);
        recon.Balances.Should().BeTrue();
        (await sink.ActiveRowsAsync(BeneficiaryStream.StreamName)).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_identifier_and_missing_name_are_rejected_with_reasons()
    {
        var sink = new InMemorySink();
        var audit = new CapturingAudit();
        var rows = new IReadOnlyDictionary<string, string?>[]
        {
            Row("bad-id", "Real Name", "123"),                     // invalid national id
            new Dictionary<string, string?> { ["source_id"] = "no-name", ["national_id"] = "29001010123456" }, // missing full_name
        };

        var (recon, _) = await new BeneficiaryStream(sink, audit, Clock)
            .RunAsync(NewBatch(), DefaultConfigs.Beneficiaries(), rows, []);

        recon.Rejected.Should().Be(2);
        recon.Balances.Should().BeTrue();
        recon.Exceptions.Should().Contain(e => e.SourceId == "bad-id" && e.Reason.Contains("invalid identifier"));
        recon.Exceptions.Should().Contain(e => e.SourceId == "no-name" && e.Reason.Contains("missing required"));
    }
}
