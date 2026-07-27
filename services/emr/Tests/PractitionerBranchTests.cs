using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// Phase 18.C2 (audit R2 W7 — FR-BRN-026/027) — a practitioner may only hold availability at, and be booked
/// into, a branch they are actually assigned to.
///
/// provider-service has exposed <c>serves-branch</c> since 14.5; emr had zero <c>practitioner</c> references
/// and never called it. The consequence is not an error message anyone sees — it is a patient arriving at a
/// clinic, after travelling there, for an appointment the system confirmed with a doctor who does not work
/// at that site.
/// </summary>
public class PractitionerBranchTests
{
    private static readonly Guid Doctor = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Branch = new("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void A_definite_no_refuses_with_a_reason_the_desk_can_act_on()
    {
        var reason = PractitionerBranchRules.Refuse(servesBranch: false, Doctor, Branch);

        reason.Should().NotBeNull();
        // "422 invalid" tells the receptionist nothing. The message has to say what is wrong and what to do,
        // because the person reading it is standing in front of a patient.
        reason.Should().Contain("no active assignment")
            .And.Contain(Branch.ToString())
            .And.Contain("choose a doctor who works here");
    }

    [Fact]
    public void A_definite_yes_permits()
    {
        PractitionerBranchRules.Refuse(servesBranch: true, Doctor, Branch).Should().BeNull();
    }

    [Fact]
    public void An_unknown_answer_permits_and_is_not_treated_as_a_no()
    {
        // The judgement call, stated once and tested. provider-service being briefly unreachable must not
        // stop a clinic booking patients: the harm of refusing every booking during an outage is larger and
        // more immediate than the mis-assignment this guards against. Turning "don't know" into "no" is also
        // how a safety check gets switched off in production after the first incident.
        PractitionerBranchRules.Refuse(servesBranch: null, Doctor, Branch).Should().BeNull();
    }

    [Fact]
    public void Both_gates_share_one_rule()
    {
        // FR-BRN-026 (availability) and 027 (booking) are separate requirements with the same answer. They
        // call the same function so they cannot drift — and they are BOTH needed: a walk-in is slotless and
        // never passes through availability at all.
        foreach (bool? answer in new bool?[] { true, false, null })
            PractitionerBranchRules.Refuse(answer, Doctor, Branch)
                .Should().Be(PractitionerBranchRules.Refuse(answer, Doctor, Branch));

        PractitionerBranchRules.ProblemType.Should().Be("urn:hbmp:practitioner-not-at-branch");
    }
}
