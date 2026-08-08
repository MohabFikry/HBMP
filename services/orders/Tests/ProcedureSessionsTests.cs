using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>
/// 29.2 / design 45 §2 — <b>sessions authorised ≠ sessions requested</b>.
///
/// <para>The prompt calls this "the easiest thing here to get backwards", and the reason it is worth its own
/// test file is that getting it backwards fails silently: ten sessions delivered against a six-session
/// approval reads as a completed course from the centre's queue AND from the ordering doctor's worklist. No
/// counter disagrees with another; the beneficiary is simply over-supplied by four and their benefit
/// over-consumed by four.</para>
/// </summary>
public class ProcedureSessionsTests
{
    [Fact]
    public void A_partial_approval_of_ten_to_six_makes_six_deliverable()
    {
        ProcedureSessions.Deliverable(requested: 10, approved: 6).Should().Be(6);
    }

    [Fact]
    public void An_undecided_order_delivers_nothing_rather_than_what_was_requested()
    {
        // Absence of a decision is not assent. This is the reading that lets a centre deliver a full course
        // against an authorisation that never existed.
        ProcedureSessions.Deliverable(requested: 10, approved: null).Should().Be(0);
    }

    [Fact]
    public void An_approval_larger_than_the_request_never_inflates_the_order()
    {
        ProcedureSessions.Deliverable(requested: 6, approved: 10).Should().Be(6);
    }

    [Fact]
    public void Applying_an_approval_narrows_what_may_be_delivered_but_not_what_was_asked()
    {
        // Both numbers survive. Overwriting the request would destroy the only signal that partial approval is
        // happening at all — "how often are we approving less than we ask for?" stops being answerable.
        var line = Line(requested: 10);

        ProcedureSessions.ApplyApproval(line, approvedQuantity: 6);

        line.QuantityOrdered.Should().Be(6, "consume meters against the APPROVED scope");
        line.RequestedQuantity.Should().Be(10, "what the doctor asked for does not stop being true");
        line.Status.Should().Be(OrderLineStatus.Active);
    }

    [Fact]
    public void A_fully_rejected_approval_cancels_the_line()
    {
        var line = Line(requested: 10);

        ProcedureSessions.ApplyApproval(line, approvedQuantity: 0);

        line.QuantityOrdered.Should().Be(0);
        line.Status.Should().Be(OrderLineStatus.Cancelled);
    }

    [Fact]
    public void An_approval_cut_below_what_is_already_delivered_never_un_delivers_a_session()
    {
        // A beneficiary who has attended four sessions has attended four. A retroactive cut to two would
        // violate quantity_consumed <= quantity_ordered and, more to the point, would imply un-attending two
        // real appointments. The overage is a case for the approvals team, not something to fix by rewriting
        // what happened.
        var line = Line(requested: 10);
        line.QuantityConsumed = 4;

        ProcedureSessions.ApplyApproval(line, approvedQuantity: 2);

        line.QuantityOrdered.Should().Be(4, "never below what has already been consumed");
        line.QuantityConsumed.Should().Be(4);
        line.Status.Should().Be(OrderLineStatus.Completed, "nothing further may be delivered");
    }

    [Fact]
    public void Progress_reads_the_same_from_both_ends()
    {
        // "4 of 6 sessions delivered", in the centre's queue and in the doctor's worklist. A course that reads
        // differently at each end is a course somebody delivers twice.
        var line = Line(requested: 10);
        ProcedureSessions.ApplyApproval(line, approvedQuantity: 6);
        line.QuantityConsumed = 4;

        ProcedureSessions.Progress(line).Should().Be((4, 6));
    }

    [Fact]
    public void Sessions_are_metered_by_the_existing_consume_rule_not_a_parallel_counter()
    {
        // Design 45 §2: "Sessions are the order line's quantity. Not a parallel counter." The proof that this
        // holds is that the ORDINARY consume validation governs a session order with no special case — the
        // same atomic, idempotent, no-reuse rule that protects every other line, with partial fulfilment
        // leaving the remainder active.
        var line = Line(requested: 10);
        ProcedureSessions.ApplyApproval(line, approvedQuantity: 6);
        var order = new InvestigationOrder
        {
            OrderId = line.OrderId, OrderNo = "ORD-2026-000901", OrderType = OrderType.Procedure,
            Status = OrderStatus.Active, Lines = [line],
        };

        // Five of the six approved sessions delivered — the remainder stays active, which is the platform's
        // existing partial-fulfilment invariant rather than anything new.
        line.QuantityConsumed = 5;
        OrderConsume.Validate(order, [new ConsumeLineRequest(line.OrderLineId, 1)])
            .Should().Be(ConsumeError.None);

        // The seventh session — against the REQUESTED ten but the APPROVED six — is refused by the shared
        // over-consume rule. Nothing here knows what a "session" is, and that is the point.
        line.QuantityConsumed = 6;
        OrderConsume.Validate(order, [new ConsumeLineRequest(line.OrderLineId, 1)])
            .Should().Be(ConsumeError.OverConsume);
    }

    private static OrderLine Line(decimal requested) => new()
    {
        OrderLineId = Guid.NewGuid(),
        OrderId = Guid.NewGuid(),
        CodeSystem = CodeSystem.CPT,
        Code = "97110",
        ProcedureTypeCode = "Physiotherapy",
        RequestedQuantity = requested,
        QuantityOrdered = requested,
        QuantityConsumed = 0,
        Status = OrderLineStatus.Active,
    };
}
