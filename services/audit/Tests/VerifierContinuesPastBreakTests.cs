using FluentAssertions;
using Mersal.Audit.Client;

namespace Mersal.Audit.Tests;

/// <summary>
/// A single broken record must not blind the verifier to everything after it.
///
/// <para><b>Why this is worse than the break it hides.</b> <c>Verify</c> returned at the FIRST break. On the
/// live trail that meant one record damaged by the jsonb pre-image defect (see
/// docs/audit-chain-integrity-2026-08.md) left <b>33,404 of 33,407 records never reached</b> — including
/// every record written afterwards. The verifier is the only thing that would report real tampering, and a
/// known-bad row at index 28 switched it off for the rest of the partition.</para>
///
/// <para>So a break must be REPORTED and then STEPPED OVER, not treated as the end of the chain.</para>
/// </summary>
public class VerifierContinuesPastBreakTests
{
    private static AuditEvent Rec(string entityId, string? prev) => HashChain.Chain(new AuditEvent
    {
        AuditEventId = Guid.NewGuid(),
        ServiceName = "orders-service", SourceService = "orders-service",
        EntityType = "investigation_order", EntityId = entityId,
        Action = AuditAction.Create,
        OccurredAt = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero),
    }, prev);

    /// <summary>A three-record chain whose MIDDLE record has been tampered with, leaving the rest correctly
    /// chained onto its STORED hash — exactly the shape the live trail is in.</summary>
    private static List<AuditEvent> ChainWithTamperedMiddle()
    {
        var a = Rec("A", null);
        var b = Rec("B", a.RecordHash);
        var c = Rec("C", b.RecordHash);

        // Tamper with B's CONTENT after the fact. Its stored record_hash and C's prev_hash are untouched,
        // so only B's own content hash is wrong — a corrupted record, not a broken linkage.
        return [a, b with { EntityId = "B-TAMPERED" }, c];
    }

    [Fact]
    public void A_break_is_reported_and_the_records_after_it_are_still_verified()
    {
        var result = HashChain.Verify(ChainWithTamperedMiddle());

        result.IsIntact.Should().BeFalse();
        result.Breaks.Should().HaveCount(1, "only the middle record is damaged");
        result.Breaks[0].Index.Should().Be(1);
        // The whole point: index 2 was REACHED and found sound, rather than never being looked at.
        result.RecordsVerified.Should().Be(3);
    }

    [Fact]
    public void A_second_break_after_the_first_is_found()
    {
        // The failure the old behaviour permitted: real tampering hiding behind a known-bad record.
        var chain = ChainWithTamperedMiddle();
        chain[2] = chain[2] with { EntityId = "C-TAMPERED" };

        var result = HashChain.Verify(chain);

        result.Breaks.Should().HaveCount(2, "a break after a known break must still be reported");
        result.Breaks.Select(b => b.Index).Should().Equal(1, 2);
    }

    [Fact]
    public void An_intact_chain_still_reports_intact()
    {
        var a = Rec("A", null);
        var b = Rec("B", a.RecordHash);

        var result = HashChain.Verify([a, b]);

        result.IsIntact.Should().BeTrue();
        result.Breaks.Should().BeEmpty();
        result.RecordsVerified.Should().Be(2);
    }

    [Fact]
    public void A_corrupted_record_does_not_cascade_into_every_record_after_it()
    {
        // Continuation must resume from the record's STORED hash, because that is what the next record was
        // actually chained onto. Resuming from the RECOMPUTED hash would make every subsequent record report
        // a prev_hash mismatch — one real break rendered as thousands, which is just as unreadable as none.
        var result = HashChain.Verify(ChainWithTamperedMiddle());

        result.Breaks.Should().ContainSingle();
        result.Breaks[0].Reason.Should().Contain("record_hash mismatch");
    }

    [Fact]
    public void The_first_break_is_still_exposed_for_callers_that_only_want_one()
    {
        // Backwards compatibility: the existing alerter and its tests read BrokenAtIndex/BrokenRecordId.
        var result = HashChain.Verify(ChainWithTamperedMiddle());

        result.BrokenAtIndex.Should().Be(1);
        result.BrokenRecordId.Should().NotBeNull();
        result.Reason.Should().NotBeNullOrEmpty();
    }
}
