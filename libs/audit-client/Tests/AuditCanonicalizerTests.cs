using FluentAssertions;
using Mersal.Audit.Client;

namespace Mersal.Audit.Client.Tests;

public class AuditCanonicalizerTests
{
    private static AuditEvent Base() => new()
    {
        AuditEventId = Guid.Parse("018f9c4e-0000-7000-8000-000000000001"),
        ServiceName = "orders-service",
        SourceService = "orders-service",
        EntityType = "order_line",
        EntityId = "ORD-1:line-1",
        Action = AuditAction.Consume,
        OccurredAt = new DateTimeOffset(2026, 7, 22, 9, 30, 15, 123, TimeSpan.Zero),
        PrevHash = HashChain.Genesis,
    };

    [Fact]
    public void Canonical_form_is_stable_across_calls()
    {
        var e = Base();
        AuditCanonicalizer.CanonicalString(e).Should().Be(AuditCanonicalizer.CanonicalString(e));
    }

    [Fact]
    public void Record_hash_excluded_from_canonical_form()
    {
        var withoutHash = Base();
        var withHash = Base() with { RecordHash = "deadbeef" };

        AuditCanonicalizer.CanonicalString(withoutHash)
            .Should().Be(AuditCanonicalizer.CanonicalString(withHash));
    }

    [Fact]
    public void Any_field_change_changes_canonical_form()
    {
        var a = Base();
        var b = Base() with { EntityId = "ORD-1:line-2" };

        AuditCanonicalizer.CanonicalString(a).Should().NotBe(AuditCanonicalizer.CanonicalString(b));
    }

    [Fact]
    public void Timestamp_normalized_to_utc_millis()
    {
        var utc = Base();
        var offset = Base() with { OccurredAt = new DateTimeOffset(2026, 7, 22, 11, 30, 15, 123, TimeSpan.FromHours(2)) };

        // Same instant expressed in a different offset → identical canonical form.
        AuditCanonicalizer.CanonicalString(utc).Should().Be(AuditCanonicalizer.CanonicalString(offset));
    }
}
