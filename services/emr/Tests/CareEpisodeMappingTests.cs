using System.Text.Json;
using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Mersal.Events;

namespace Mersal.Emr.Tests;

/// <summary>
/// Turning a sibling service's event into a step on the patient's episode (ADR-0031).
///
/// <para>No broker and no database: the translation is where the judgement lives, so it is written as a pure
/// function and tested as one. What the consumer adds around it is transport.</para>
///
/// <para>The load-bearing test in this file is <see cref="A_step_names_the_act_and_carries_none_of_its_content"/>.
/// The rest prove the mapping is complete and refuses what it cannot place; that one proves it is SAFE, and
/// it is the reason this timeline can be shown to reception at all.</para>
/// </summary>
public class CareEpisodeMappingTests
{
    private static readonly Guid Enc = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Doctor = "sub-doctor-karim";

    /// <summary>A realistic payload for one event type — the real fields these services publish, including the
    /// clinical ones, because a mapping tested only against sanitised input proves nothing about leakage.</summary>
    private static string PayloadFor(string eventType) => eventType switch
    {
        "OrderCreated" => Json(new
        {
            tenantId = "t-1", orderId = Guid.NewGuid(), orderNo = "ORD-2026-000014",
            beneficiaryId = Guid.NewGuid(), encounterId = Enc, orderType = "Lab", orderedByUserId = Doctor,
        }),
        "OrderPendingApproval" => Json(new
        {
            tenantId = "t-1", orderId = Guid.NewGuid(), orderNo = "ORD-2026-000014",
            reason = "high-cost-imaging", beneficiaryId = Guid.NewGuid(), encounterId = Enc,
            orderedByUserId = Doctor,
        }),
        "OrderCancelled" => Json(new
        {
            tenantId = "t-1", orderId = Guid.NewGuid(), orderNo = "ORD-2026-000014",
            beneficiaryId = Guid.NewGuid(), encounterId = Enc, cancelledByUserId = Doctor,
            reason = "ordered in error",
        }),
        "OrderLinesConsumed" => Json(new
        {
            orderId = Guid.NewGuid(), orderType = "Lab", tenantId = "t-1", beneficiaryId = Guid.NewGuid(),
            encounterId = Enc, orderNo = "ORD-2026-000014", benefitCategory = "LAB",
            serviceDate = "2026-08-02", providerId = Guid.NewGuid(),
            lines = new[] { new { orderLineId = Guid.NewGuid(), quantity = 1m } },
            idempotencyKey = "k-1",
        }),
        "OrderResultUploaded" => Json(new
        {
            tenantId = "t-1", orderId = Guid.NewGuid(), lineId = Guid.NewGuid(),
            fulfillmentId = Guid.NewGuid(), orderNo = "ORD-2026-000014",
            orderingProviderId = Guid.NewGuid(), beneficiaryId = Guid.NewGuid(), encounterId = Enc,
            approvalGated = false, resultDocumentId = (Guid?)null, sensitivityLevel = "Standard",
        }),
        "RxCreated" => Json(new
        {
            tenantId = "t-1", prescriptionId = Guid.NewGuid(), rxNo = "RX-2026-000031",
            beneficiaryId = Guid.NewGuid(), encounterId = Enc, orderedByUserId = Doctor,
        }),
        "RxCancelled" => Json(new
        {
            tenantId = "t-1", prescriptionId = Guid.NewGuid(), rxNo = "RX-2026-000031",
            beneficiaryId = Guid.NewGuid(), encounterId = Enc, cancelledByUserId = Doctor,
            reason = "substituted",
        }),
        "RxLinesDispensed" => Json(new
        {
            prescriptionId = Guid.NewGuid(), prescriptionLineId = Guid.NewGuid(), tenantId = "t-1",
            beneficiaryId = Guid.NewGuid(), encounterId = Enc, rxNo = "RX-2026-000031",
            benefitCategory = "PHARMACY", serviceDate = "2026-08-02", providerId = Guid.NewGuid(),
            quantity = 20m, batchNo = "B-77", idempotencyKey = "k-2",
        }),
        _ => Json(new
        {
            tenantId = "t-1", authorizationId = Guid.NewGuid(), authNo = "AUTH-2026-000009",
            beneficiaryId = Guid.NewGuid(), encounterId = Enc, source = "OrderLine",
            sourceRef = Guid.NewGuid().ToString(), releasesDownstream = true, breakGlass = false,
            priority = "Routine", reviewerId = "sub-reviewer-hala", tatSeconds = 900, slaBreached = false,
        }),
    };

    [Fact]
    public void Every_event_on_the_care_feed_maps_to_a_step()
    {
        // The mirror's allow-list and this switch are two lists that must agree. A type on the feed that maps
        // to nothing is a message emr pays to receive and then throws away — and the symptom is a missing
        // step, which reads exactly like "it never happened".
        foreach (var type in CareFeed.EventTypes)
            CareEpisodeMapping.For(type, PayloadFor(type))
                .Should().NotBeNull("{0} is mirrored to emr and must produce a step", type);
    }

    [Theory]
    [InlineData("OrderCreated", CareSteps.OrderPlaced, "ORD-2026-000014", CareStepSources.Orders)]
    [InlineData("OrderPendingApproval", CareSteps.OrderSentForApproval, "ORD-2026-000014", CareStepSources.Orders)]
    [InlineData("OrderCancelled", CareSteps.OrderCancelled, "ORD-2026-000014", CareStepSources.Orders)]
    [InlineData("OrderLinesConsumed", CareSteps.SampleConsumed, "ORD-2026-000014", CareStepSources.Orders)]
    [InlineData("OrderResultUploaded", CareSteps.ResultReported, "ORD-2026-000014", CareStepSources.Orders)]
    [InlineData("RxCreated", CareSteps.PrescriptionWritten, "RX-2026-000031", CareStepSources.Pharmacy)]
    [InlineData("RxCancelled", CareSteps.PrescriptionCancelled, "RX-2026-000031", CareStepSources.Pharmacy)]
    [InlineData("RxLinesDispensed", CareSteps.MedicineDispensed, "RX-2026-000031", CareStepSources.Pharmacy)]
    [InlineData("AuthApproved", CareSteps.AuthorizationDecided, "AUTH-2026-000009", CareStepSources.Approvals)]
    [InlineData("AuthRejected", CareSteps.AuthorizationDecided, "AUTH-2026-000009", CareStepSources.Approvals)]
    public void An_event_becomes_the_step_a_person_would_recognise(
        string eventType, string step, string reference, string source)
    {
        // The wire says "OrderLinesConsumed"; a member reading their own history needs "sample taken". And the
        // reference is a BUSINESS key — ORD-2026-000014 is a thing a desk can read out and look up, which an
        // internal uuid is not.
        var draft = CareEpisodeMapping.For(eventType, PayloadFor(eventType));

        draft.Should().NotBeNull();
        draft!.Step.Should().Be(step);
        draft.Reference.Should().Be(reference);
        draft.Source.Should().Be(source);
        draft.EncounterId.Should().Be(Enc);
    }

    [Fact]
    public void A_step_names_the_act_and_carries_none_of_its_content()
    {
        // RECEPTION reads this timeline. That is the entire reason for the rule: the desk is entitled to know
        // a prescription was written on this visit and is structurally forbidden the medicine.
        var payload = Json(new
        {
            tenantId = "t-1", prescriptionId = Guid.NewGuid(), rxNo = "RX-2026-000031",
            beneficiaryId = Guid.NewGuid(), encounterId = Enc, orderedByUserId = Doctor,
            // Fields no pharmacy event carries today — present here precisely so this test keeps holding if
            // one starts to. The mapping must ignore what it does not need, not merely what is absent.
            drugId = Guid.NewGuid(), drugName = "Metformin 500mg", dose = "1 tablet twice daily",
            diagnosis = "E11.9", note = "poorly controlled diabetes",
        });

        var draft = CareEpisodeMapping.For("RxCreated", payload);

        draft.Should().NotBeNull();
        var written = JsonSerializer.Serialize(draft);
        written.Should().NotContain("Metformin", "the medicine is care, not an appointment status");
        written.Should().NotContain("E11.9", "nor is the diagnosis it was written for");
        written.Should().NotContain("twice daily");
        written.Should().NotContain("poorly controlled");
        written.Should().Contain("RX-2026-000031", "the business key IS the step's reference — it is the door, not the room");
    }

    [Fact]
    public void An_event_with_no_encounter_produces_no_step()
    {
        // orders and pharmacy type the column non-nullable, so "raised outside a visit" arrives as all-zeroes
        // rather than as null. Attaching that to an episode would mean attaching it to whichever encounter
        // happens to have the empty guid — i.e. to nobody, or worse, to everybody.
        var missing = Json(new { tenantId = "t-1", orderNo = "ORD-2026-000014" });
        var empty = Json(new { tenantId = "t-1", orderNo = "ORD-2026-000014", encounterId = Guid.Empty });

        CareEpisodeMapping.For("OrderCreated", missing).Should().BeNull();
        CareEpisodeMapping.For("OrderCreated", empty).Should().BeNull();
    }

    [Fact]
    public void Requesting_more_information_is_not_a_decision()
    {
        // AuthInfoRequested lands on the same append-only decision ledger as the real decisions, which is
        // exactly why it is tempting to step it as one. A desk shown "authorization decided" stops chasing —
        // and this is the case where somebody must keep chasing.
        CareFeed.Includes("AuthInfoRequested").Should().BeFalse();
        CareEpisodeMapping.For("AuthInfoRequested", PayloadFor("AuthApproved")).Should().BeNull();
    }

    [Fact]
    public void Anything_unrecognised_or_unparseable_is_simply_not_a_step()
    {
        // Never an exception. The consumer acks these, and a mapping that threw would turn "an event we do not
        // record" into a dead-lettered message and a red log line.
        CareEpisodeMapping.For("OrderActivated", PayloadFor("OrderCreated")).Should().BeNull();
        CareEpisodeMapping.For("OrderCreated", "{ not json").Should().BeNull();
        CareEpisodeMapping.For("OrderCreated", "[]").Should().BeNull();
        CareEpisodeMapping.For("OrderCreated", "").Should().BeNull();
        CareEpisodeMapping.For(null, PayloadFor("OrderCreated")).Should().BeNull();
    }

    [Fact]
    public void A_step_names_the_person_when_the_event_does_and_stays_silent_when_it_does_not()
    {
        // The clinician-authored acts carry a subject the timeline can resolve to a name.
        CareEpisodeMapping.For("OrderCreated", PayloadFor("OrderCreated"))!.Actor.Should().Be(Doctor);
        CareEpisodeMapping.For("RxCreated", PayloadFor("RxCreated"))!.Actor.Should().Be(Doctor);

        // The fulfilment acts do not. The payload names the performing FACILITY, not a person emr could put a
        // name to — and a truncated uuid rendered under "who did this" is worse than an honest blank.
        CareEpisodeMapping.For("OrderLinesConsumed", PayloadFor("OrderLinesConsumed"))!.Actor.Should().BeNull();
        CareEpisodeMapping.For("RxLinesDispensed", PayloadFor("RxLinesDispensed"))!.Actor.Should().BeNull();
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Json(object o) => JsonSerializer.Serialize(o, Web);
}
