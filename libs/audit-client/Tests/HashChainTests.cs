using FluentAssertions;
using Mersal.Audit.Client;

namespace Mersal.Audit.Client.Tests;

public class HashChainTests
{
    private static AuditEvent NewEvent(string entityId, AuditAction action = AuditAction.Create) => new()
    {
        AuditEventId = Guid.NewGuid(),
        ServiceName = "patient-service",
        SourceService = "patient-service",
        EntityType = "beneficiary",
        EntityId = entityId,
        Action = action,
        OccurredAt = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
    };

    private static List<AuditEvent> BuildChain(params AuditEvent[] events)
    {
        var chained = new List<AuditEvent>();
        string? prev = HashChain.Genesis;
        foreach (var e in events)
        {
            var c = HashChain.Chain(e, prev);
            chained.Add(c);
            prev = c.RecordHash;
        }
        return chained;
    }

    [Fact]
    public void Chain_sets_prev_and_record_hash()
    {
        var e = HashChain.Chain(NewEvent("MRS-M-1"), prevHash: null);

        e.PrevHash.Should().Be(HashChain.Genesis);
        e.RecordHash.Should().NotBeNullOrEmpty().And.HaveLength(64); // sha256 hex
    }

    [Fact]
    public void Record_hash_is_deterministic_for_same_content()
    {
        var e = NewEvent("MRS-M-1") with { PrevHash = HashChain.Genesis };
        HashChain.ComputeRecordHash(e).Should().Be(HashChain.ComputeRecordHash(e));
    }

    [Fact]
    public void Intact_chain_verifies_ok()
    {
        var chain = BuildChain(NewEvent("A"), NewEvent("B"), NewEvent("C"));

        HashChain.Verify(chain).IsIntact.Should().BeTrue();
    }

    [Fact]
    public void Tampering_a_field_is_detected()
    {
        var chain = BuildChain(NewEvent("A"), NewEvent("B"), NewEvent("C"));

        // Attacker edits the middle record's entity id but leaves the stored hashes.
        chain[1] = chain[1] with { EntityId = "TAMPERED" };

        var result = HashChain.Verify(chain);
        result.IsIntact.Should().BeFalse();
        result.BrokenAtIndex.Should().Be(1);
        result.Reason.Should().Contain("record_hash mismatch");
    }

    [Fact]
    public void Deleting_a_record_breaks_the_chain()
    {
        var chain = BuildChain(NewEvent("A"), NewEvent("B"), NewEvent("C"));
        chain.RemoveAt(1); // remove B → C.prev_hash no longer matches A.record_hash

        var result = HashChain.Verify(chain);
        result.IsIntact.Should().BeFalse();
        result.Reason.Should().Contain("prev_hash mismatch");
    }

    [Fact]
    public void Reordering_records_breaks_the_chain()
    {
        var chain = BuildChain(NewEvent("A"), NewEvent("B"), NewEvent("C"));
        (chain[1], chain[2]) = (chain[2], chain[1]); // swap B and C

        HashChain.Verify(chain).IsIntact.Should().BeFalse();
    }

    [Fact]
    public void Inserting_a_forged_record_breaks_the_chain()
    {
        var chain = BuildChain(NewEvent("A"), NewEvent("B"));
        var forged = HashChain.Chain(NewEvent("FORGED"), prevHash: chain[1].RecordHash);
        chain.Insert(1, forged); // wrong position → index 1's prev_hash won't match A

        HashChain.Verify(chain).IsIntact.Should().BeFalse();
    }
}
