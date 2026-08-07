using System.Text.Json;
using FluentAssertions;
using Mersal.Orders.Api;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>
/// 29.2b / design 45 §2b — the external centre's projection, asserted over the SERIALIZED payload.
///
/// <para>Over the serialized bytes on purpose. Asserting on the DTO's properties proves what the type
/// declares; asserting on the JSON proves what actually crosses the wire, which is the only thing an external
/// organisation can read. The two differ the moment somebody adds a field to a base record, a serializer
/// setting changes, or a nullable field is populated "just for the internal case".</para>
/// </summary>
public class ProcedureProviderProjectionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_payload_carries_no_diagnosis_beyond_what_the_doctor_chose_to_share()
    {
        var json = Serialize(sharedContext: "Post-op knee rehabilitation, ACL repair 12 Feb.");

        // What the doctor DELIBERATELY disclosed is present — a physiotherapist genuinely needs to know why
        // they are treating someone.
        json.Should().Contain("Post-op knee rehabilitation");

        // Everything else clinical is not merely empty — it has no field at all.
        foreach (var forbidden in new[]
                 { "diagnosis", "icd", "encounter", "note", "emr", "allergy", "medication", "prescription" })
        {
            json.ToLowerInvariant().Should().NotContain($"\"{forbidden}",
                "an external centre sees no clinical data beyond the ordering doctor's explicit disclosure");
        }
    }

    [Fact]
    public void The_payload_carries_no_money()
    {
        // A delivering centre is paid under its contract, which it already knows. Coverage amounts describe
        // the BENEFICIARY's entitlement, and a centre that can see how much benefit remains has both a reason
        // and a means to shape what it recommends.
        var json = Serialize().ToLowerInvariant();

        foreach (var forbidden in new[]
                 { "coverage", "costshare", "cost_share", "copay", "claim", "price", "amount", "spend", "egp" })
        {
            json.Should().NotContain($"\"{forbidden}", "coverage amounts, cost-share and claim values never leave");
        }
    }

    [Fact]
    public void A_shared_context_that_was_never_set_is_absent_rather_than_reading_as_no_diagnosis()
    {
        // Null means NOT DISCLOSED. It must never render to the centre as "this patient has no relevant
        // history" — absence of data is never a clean result, and a physiotherapist who reads it that way
        // treats someone as uncomplicated who is not.
        var item = Item(sharedContext: null);

        item.SharedClinicalContext.Should().BeNull();
    }

    [Fact]
    public void Progress_reads_from_the_authorised_scope_and_matches_the_doctors_worklist()
    {
        var line = Line(requested: 10);
        ProcedureSessions.ApplyApproval(line, approvedQuantity: 6);
        line.QuantityConsumed = 4;

        var item = ProcedureQueueItem.From(Order(), line, "Amal Hassan", null, DateTimeOffset.UtcNow);

        item.SessionsAuthorised.Should().Be(6, "authorised, never requested");
        item.SessionsDelivered.Should().Be(4);
        item.SessionsRemaining.Should().Be(2);
        item.ProgressLabel.Should().Be("4 of 6 sessions delivered");
    }

    [Fact]
    public void The_requested_count_never_reaches_the_centre()
    {
        // If the doctor asked for ten and six were approved, the centre must not learn that ten were asked
        // for — it is not their decision to revisit, and a centre that sees the gap has a standing invitation
        // to ask the beneficiary to seek the rest.
        var line = Line(requested: 10);
        ProcedureSessions.ApplyApproval(line, approvedQuantity: 6);

        var item = ProcedureQueueItem.From(Order(), line, "Amal Hassan", null, DateTimeOffset.UtcNow);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(item, Web));

        // Asserted over the payload's FIELD NAMES rather than by searching for the literal "10": a guid or a
        // timestamp in the same document contains "10" often enough that such a test passes or fails by luck,
        // which is worse than not having it.
        var fields = doc.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();

        fields.Should().NotContain(f => f.Contains("request"),
            "the centre is not told how many were asked for — it is not their decision to revisit");
        doc.RootElement.GetProperty("sessionsAuthorised").GetInt32().Should().Be(6);
    }

    [Fact]
    public void Expiry_is_computed_against_the_clock_not_read_from_the_status()
    {
        // The sweeper runs hourly, so between lapsing and being swept the row still says Active. A queue that
        // trusted the status would offer the centre work that consume then refuses.
        var order = Order();
        order.ExpiresAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        order.Status = OrderStatus.Active;

        ProcedureQueueItem.From(order, Line(6), "Amal Hassan", null,
            new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero)).Expired.Should().BeTrue();
        ProcedureQueueItem.From(order, Line(6), "Amal Hassan", null,
            new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero)).Expired.Should().BeFalse();
    }

    private static string Serialize(string? sharedContext = "Post-op knee rehabilitation, ACL repair 12 Feb.") =>
        JsonSerializer.Serialize(Item(sharedContext), Web);

    private static ProcedureQueueItem Item(string? sharedContext) =>
        ProcedureQueueItem.From(Order(sharedContext), Line(6), "Amal Hassan", null, DateTimeOffset.UtcNow);

    private static InvestigationOrder Order(string? sharedContext = null) => new()
    {
        OrderId = Guid.NewGuid(),
        OrderNo = "ORD-2026-000900",
        OrderType = OrderType.Procedure,
        Status = OrderStatus.Active,
        BeneficiaryId = Guid.NewGuid(),
        AssignedProviderId = Guid.NewGuid(),
        AuthorizationId = Guid.NewGuid(),
        SharedClinicalContext = sharedContext,
        SharedContextBy = sharedContext is null ? null : "dr-yasmine",
        SharedContextAt = sharedContext is null ? null : DateTimeOffset.UtcNow,
    };

    private static OrderLine Line(decimal requested) => new()
    {
        OrderLineId = Guid.NewGuid(),
        CodeSystem = CodeSystem.CPT,
        Code = "97110",
        Description = "Therapeutic exercise",
        ProcedureTypeCode = "Physiotherapy",
        RequestedQuantity = requested,
        QuantityOrdered = requested,
    };
}
