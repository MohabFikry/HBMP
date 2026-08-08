using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Audit.Domain;

namespace Mersal.Audit.Tests;

/// <summary>
/// 29.1 / design 45 §1 (c) — historical audit rows keep their old identifiers FOREVER, and readers resolve
/// them through a display alias.
///
/// <para>The invariant under test is not "the alias maps a string". It is that the rename left the hash chain
/// untouched — which is the property that makes the audit trail evidence rather than a log.</para>
/// </summary>
public class LegacyIdentifierDisplayTests
{
    [Theory]
    [InlineData("imaging_tech", "radiology_tech")]
    [InlineData("Imaging", "Radiology")]
    public void A_retired_identifier_reads_under_todays_name(string stored, string expected)
    {
        LegacyIdentifierDisplay.Display(stored).Should().Be(expected);
        LegacyIdentifierDisplay.IsRetired(stored).Should().BeTrue();
    }

    [Theory]
    [InlineData("radiology_tech")]
    [InlineData("lab_tech")]
    [InlineData("doctor")]
    public void A_name_that_was_never_renamed_is_returned_unchanged(string stored)
    {
        LegacyIdentifierDisplay.Display(stored).Should().Be(stored);
        LegacyIdentifierDisplay.IsRetired(stored).Should().BeFalse();
    }

    [Fact]
    public void An_absent_actor_role_stays_absent()
    {
        // Null in, null out. Resolving absence into a name would invent an actor for a row that recorded none.
        LegacyIdentifierDisplay.Display(null).Should().BeNull();
        LegacyIdentifierDisplay.IsRetired(null).Should().BeFalse();
    }

    [Fact]
    public void Displaying_a_renamed_identifier_does_not_change_the_record_hash()
    {
        // THE point of the alias, stated as the thing it must not do. If the rename had been applied as an
        // UPDATE to actor_role, this row's canonical bytes would change, its record_hash would no longer
        // match, and AuditVerifier would report the partition as tampered — correctly. The alias is a read
        // projection precisely so that the bytes that were hashed stay the bytes that are stored.
        var written = Chained(actorRole: "imaging_tech");

        var displayed = LegacyIdentifierDisplay.Display(written.ActorRole);

        displayed.Should().Be("radiology_tech");
        written.ActorRole.Should().Be("imaging_tech", "the stored row is never rewritten");
        HashChain.ComputeRecordHash(written with { RecordHash = null }).Should().Be(written.RecordHash,
            "the row still hashes to what it hashed to before the rename");
    }

    [Fact]
    public void Rewriting_the_stored_role_would_break_the_chain()
    {
        // The counter-proof. Without this the test above only shows that doing nothing changes nothing; this
        // shows that doing the OBVIOUS thing — updating the row to the new name — is what the design forbids,
        // and why. If this ever goes green, actor_role has stopped being part of the canonical bytes and the
        // audit trail no longer attests who acted.
        var written = Chained(actorRole: "imaging_tech");

        var rewritten = written with { ActorRole = "radiology_tech" };

        HashChain.ComputeRecordHash(rewritten with { RecordHash = null }).Should().NotBe(written.RecordHash,
            "rewriting a hash-chained field is exactly the tampering the chain exists to detect");
    }

    private static AuditEvent Chained(string actorRole)
    {
        var e = new AuditEvent
        {
            AuditEventId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
            ServiceName = "orders-service",
            SourceService = "orders-service",
            EntityType = "investigation_order",
            EntityId = "ORD-2026-000900",
            Action = AuditAction.Consume,
            ActorUserId = "d3a91c4b-7e20-4f18-9c61-8a4e2f0b7d93",
            ActorRole = actorRole,
            OccurredAt = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero),
        };
        return HashChain.Chain(e, prevHash: null);
    }
}
