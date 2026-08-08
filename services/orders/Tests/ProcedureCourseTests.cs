using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>
/// 31.1 — an OP-Procedure order is ONE COURSE: one kind, one session count, and a quantity PER SESSION.
///
/// <para><b>This reverses design 45 §2's "sessions ARE the quantity".</b> That model cannot express what an
/// outpatient procedure order actually is. A physiotherapy course is one clinical decision that may involve
/// several billable items per attendance; with the kind and the session count on each LINE, a two-item course
/// could be composed as six sessions of one and eight of the other — not a course any centre can deliver —
/// and there was nowhere to say "three of these per session", because the quantity slot was spent on the
/// session count.</para>
///
/// <para><b>What did NOT change is the metered total.</b> <c>QuantityOrdered</c> is still what consume meters
/// against, approvals narrow and the centre's queue counts down; it is now <c>sessions x per-session</c>.
/// Sessions delivered is DERIVED from it rather than stored, because a stored second counter is exactly the
/// parallel counter §2 was right to forbid.</para>
/// </summary>
public class ProcedureCourseTests
{
    [Fact]
    public void The_metered_total_is_sessions_times_the_per_session_quantity()
    {
        // Six sessions, two units of this item at each — twelve units to deliver, and the existing atomic
        // consume path meters them without knowing what a session is.
        ProcedureCourse.MeteredTotal(sessions: 6, quantityPerSession: 2m).Should().Be(12m);
    }

    [Fact]
    public void A_procedure_that_is_not_delivered_in_sessions_is_metered_by_its_quantity_alone()
    {
        // NULL sessions is not 1 sessions — it is "this kind is not delivered in attendances at all". The
        // total is simply the quantity, and nothing on screen offers a session field.
        ProcedureCourse.MeteredTotal(sessions: null, quantityPerSession: 3m).Should().Be(3m);
    }

    [Fact]
    public void Sessions_delivered_is_derived_from_what_was_consumed()
    {
        // "4 of 6 sessions delivered" — the SAME sentence the centre's queue and the doctor's worklist show,
        // now divided by the per-session quantity rather than read off the raw total. Eight units of a
        // two-per-session item is four attendances, not eight.
        var line = Line(quantityPerSession: 2m, orderedTotal: 12m, consumed: 8m);

        ProcedureCourse.SessionProgress(line).Should().Be((4, 6));
    }

    [Fact]
    public void A_partial_approval_narrows_the_course_and_the_session_count_follows()
    {
        // Ten sessions asked for, six approved. `ApplyApproval` narrows the metered total to the approved
        // scope; the session count the centre is shown has to follow it DOWN, or ten sessions get delivered
        // against a six-session authorisation and nothing is out of balance for anyone to notice.
        var line = Line(quantityPerSession: 2m, orderedTotal: 20m, consumed: 0m);
        line.RequestedQuantity = 20m;

        ProcedureSessions.ApplyApproval(line, approvedQuantity: 12m);

        line.QuantityOrdered.Should().Be(12m);
        ProcedureCourse.SessionProgress(line).Should().Be((0, 6));
    }

    [Fact]
    public void A_per_session_quantity_of_zero_never_divides()
    {
        // Defensive, and the defence matters: a divide-by-zero here would take down the centre's queue for
        // every order in it, not just the malformed one. The CHECK constraint forbids zero; legacy rows and
        // hand-edited data do not respect intentions.
        var line = Line(quantityPerSession: 0m, orderedTotal: 6m, consumed: 3m);

        ProcedureCourse.SessionProgress(line).Should().Be((3, 6));
    }

    [Fact]
    public void Sessions_are_rounded_DOWN_so_a_part_attendance_never_reads_as_a_whole_one()
    {
        // Three units consumed of a two-per-session item is ONE completed attendance and half of another.
        // Rounding up would report a session the patient has not had.
        var line = Line(quantityPerSession: 2m, orderedTotal: 12m, consumed: 3m);

        ProcedureCourse.SessionProgress(line).Delivered.Should().Be(1);
    }

    private static OrderLine Line(decimal quantityPerSession, decimal orderedTotal, decimal consumed) => new()
    {
        OrderLineId = Guid.NewGuid(),
        OrderId = Guid.NewGuid(),
        CodeSystem = CodeSystem.CPT,
        Code = "97110",
        QuantityPerSession = quantityPerSession,
        RequestedQuantity = orderedTotal,
        QuantityOrdered = orderedTotal,
        QuantityConsumed = consumed,
        Status = OrderLineStatus.Active,
    };
}
