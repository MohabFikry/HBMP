using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// 14.5 — only an ACTIVE member may be booked.
///
/// <para>The harm this prevents is specific: a suspended member told they have an appointment travels to a
/// clinic — often a long way, often paying for the journey — and is turned away at the desk. The refusal has
/// to happen while someone is still on the phone or at the counter and can do something about it.</para>
/// </summary>
public class BookingGateTests
{
    [Fact]
    public void Active_may_be_booked()
    {
        BookingGate.Evaluate(MemberStatus.Active).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(MemberStatus.Suspended)]
    [InlineData(MemberStatus.Expired)]
    [InlineData(MemberStatus.Blocked)]
    [InlineData(MemberStatus.Inactive)]
    [InlineData(MemberStatus.Pending)]
    public void Every_other_status_is_refused_WITH_a_remedy(MemberStatus status)
    {
        var result = BookingGate.Evaluate(status);

        result.Allowed.Should().BeFalse();
        // A refusal without a next step just moves the problem to the operator, who then has to guess.
        result.Guidance.Should().NotBeNullOrWhiteSpace();
        result.Guidance.Should().Contain("booking",
            "the guidance must speak about the booking the operator is attempting, not about starting a visit");
    }

    /// <summary>
    /// Pending is the one people argue about — booking ahead of activation feels harmless. It is not: the
    /// appointment carries a promise the platform has not made, and the person keeps it by travelling.
    /// </summary>
    [Fact]
    public void Pending_is_refused_and_points_at_activation()
    {
        BookingGate.Evaluate(MemberStatus.Pending).Guidance.Should().ContainEquivalentOf("activate");
    }

    /// <summary>
    /// The booking gate and the visit gate must AGREE on who is allowed and DIFFER on what to do about it.
    /// One shared decision, two audiences: this test fails if someone later collapses them into one call and
    /// the booking screen starts telling a call-centre agent to "complete activation before starting a visit".
    /// </summary>
    [Theory]
    [InlineData(MemberStatus.Active)]
    [InlineData(MemberStatus.Suspended)]
    [InlineData(MemberStatus.Expired)]
    [InlineData(MemberStatus.Blocked)]
    [InlineData(MemberStatus.Inactive)]
    [InlineData(MemberStatus.Pending)]
    public void The_decision_matches_the_visit_gate_even_though_the_wording_does_not(MemberStatus status)
    {
        BookingGate.Evaluate(status).Allowed.Should().Be(VisitGate.Evaluate(status).Allowed);

        if (status != MemberStatus.Active)
        {
            BookingGate.Evaluate(status).Guidance.Should().NotBe(VisitGate.Evaluate(status).Guidance,
                "the remediation differs: one is spoken to a desk with the patient present, the other to " +
                "someone arranging a date that may be weeks away");
        }
    }
}
