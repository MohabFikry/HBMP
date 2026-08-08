using FluentAssertions;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Tests;

/// <summary>29.2 / design 45 §2 — procedure type validated against the code, and sessions against the type.</summary>
public class ProcedureTypeRulesTests
{
    private static ProcedureTypeSpec Physio(int? max = 30) =>
        new("Physiotherapy", IsSessionBased: true, DefaultSessions: 6, MaxSessions: max, ["Medicine"], IsActive: true);

    private static ProcedureTypeSpec MinorSurgery() =>
        new("MinorSurgery", IsSessionBased: false, null, null, ["Surgery"], IsActive: true);

    [Fact]
    public void A_physiotherapy_type_on_a_minor_surgery_code_is_refused()
    {
        // Design 45 §2's named example. "Left unvalidated the field becomes decorative, and any reporting
        // built on it is quietly wrong" — which is worse than having no field, because the reports look right.
        var error = ProcedureTypeRules.Validate(Physio(), cptCode: "29881", requestedSessions: 6);

        error.Should().Be(ProcedureTypeError.SectionNotAllowed);
        var (en, ar) = ProcedureTypeRules.Explain(error, Physio(), "29881");
        en.Should().Contain("29881").And.Contain("Surgery");
        ar.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_physiotherapy_type_on_a_medicine_code_is_accepted()
    {
        ProcedureTypeRules.Validate(Physio(), cptCode: "97110", requestedSessions: 6)
            .Should().Be(ProcedureTypeError.None);
    }

    [Fact]
    public void An_unknown_or_retired_type_fails_closed_rather_than_falling_back()
    {
        ProcedureTypeRules.Validate(null, "97110", 6).Should().Be(ProcedureTypeError.UnknownType);
        ProcedureTypeRules.Validate(Physio() with { IsActive = false }, "97110", 6)
            .Should().Be(ProcedureTypeError.UnknownType, "a retired type must not keep being orderable");
    }

    [Fact]
    public void Sessions_follow_the_flag_not_the_name()
    {
        // The rule that stops `if (type === 'Physiotherapy')` from being written. Dialysis is session-based
        // and is not called Physiotherapy; MinorSurgery is not session-based and must refuse a session count.
        var dialysis = new ProcedureTypeSpec("Dialysis", true, 12, 156, ["Medicine"], true);

        ProcedureTypeRules.Validate(dialysis, "90935", 12).Should().Be(ProcedureTypeError.None);
        ProcedureTypeRules.Validate(MinorSurgery(), "29881", 5)
            .Should().Be(ProcedureTypeError.SessionsOnNonSessionType);
        ProcedureTypeRules.Validate(MinorSurgery(), "29881", null).Should().Be(ProcedureTypeError.None);
    }

    [Fact]
    public void A_session_based_type_requires_a_positive_session_count()
    {
        ProcedureTypeRules.Validate(Physio(), "97110", null).Should().Be(ProcedureTypeError.SessionsRequired);
        ProcedureTypeRules.Validate(Physio(), "97110", 0).Should().Be(ProcedureTypeError.SessionsRequired);
        ProcedureTypeRules.Validate(Physio(), "97110", -3).Should().Be(ProcedureTypeError.SessionsRequired);
    }

    [Fact]
    public void More_sessions_than_the_type_permits_is_refused()
    {
        ProcedureTypeRules.Validate(Physio(max: 30), "97110", 31).Should().Be(ProcedureTypeError.SessionsAboveMax);
        ProcedureTypeRules.Validate(Physio(max: 30), "97110", 30).Should().Be(ProcedureTypeError.None);
    }

    // ---- Sessions AUTHORISED ≠ sessions REQUESTED -----------------------------------------------------------

    [Fact]
    public void A_partial_approval_of_ten_to_six_yields_six_deliverable_sessions()
    {
        // Design 45 §2's acceptance case, and the one the prompt calls "the easiest thing here to get
        // backwards". Reading the REQUESTED count would over-supply the patient by four sessions and
        // over-consume their benefit by four — silently, because ten delivered against a six approval looks
        // like a completed course from both the centre's queue and the doctor's worklist.
        ProcedureTypeRules.DeliverableSessions(requestedSessions: 10, approvedSessions: 6).Should().Be(6);
    }

    [Fact]
    public void A_full_approval_yields_the_requested_count()
    {
        ProcedureTypeRules.DeliverableSessions(10, 10).Should().Be(10);
    }

    [Fact]
    public void An_approval_larger_than_the_request_never_inflates_the_order()
    {
        // Approvals cannot grant MORE than was asked for. If the data says otherwise it is a defect upstream,
        // and the safe reading is the smaller number.
        ProcedureTypeRules.DeliverableSessions(6, 10).Should().Be(6);
    }

    [Fact]
    public void An_undecided_order_delivers_nothing_rather_than_the_requested_amount()
    {
        // Absence of a decision is not a decision. Treating "not yet approved" as "approved for what was
        // asked" would let a centre deliver a full course against an authorisation that never existed.
        ProcedureTypeRules.DeliverableSessions(10, approvedSessions: null).Should().Be(0);
    }

    [Fact]
    public void A_rejected_approval_delivers_nothing()
    {
        ProcedureTypeRules.DeliverableSessions(10, approvedSessions: 0).Should().Be(0);
    }
}
