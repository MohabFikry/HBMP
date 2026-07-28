using FluentAssertions;
using Mersal.Patient.Domain;

namespace Mersal.Patient.Tests;

/// <summary>
/// 23 §1's Actor column, for the two transitions where the actor IS the control.
///
/// Blocked means "fraud/abuse confirmed". The state machine's shape was already enforced (18.A4 removed the
/// Blocked→Inactive escape hatch), but the remaining legal edge — Blocked→Active — was open to any
/// patient:write holder, so a fraud block could be lifted at the desk that placed the member's claims. The
/// spec reserves blocking for Super Admin / Director and unblocking for a Director's case review.
/// </summary>
public class LifecycleActorTests
{
    private static string? Denied(BeneficiaryStatus from, BeneficiaryStatus to, params string[] roles) =>
        BeneficiaryLifecycle.DeniedActor(from, to, roles);

    [Theory]
    [InlineData(BeneficiaryStatus.Active)]
    [InlineData(BeneficiaryStatus.Suspended)]
    public void Blocking_requires_a_director_or_super_admin(BeneficiaryStatus from)
    {
        Denied(from, BeneficiaryStatus.Blocked, "beneficiary_mgmt").Should().NotBeNull(
            "confirming fraud is a directorship decision, not a desk action (23 §1)");
        Denied(from, BeneficiaryStatus.Blocked, "medical_director").Should().BeNull();
        Denied(from, BeneficiaryStatus.Blocked, "super_admin").Should().BeNull();
    }

    [Fact]
    public void Unblocking_requires_a_director_case_review()
    {
        // Stricter than blocking on purpose: the spec's unblock row names the Director alone, because
        // lifting a fraud signal erases it — the exact move 18.A4 closed the Blocked→Inactive edge against.
        Denied(BeneficiaryStatus.Blocked, BeneficiaryStatus.Active, "beneficiary_mgmt").Should().NotBeNull();
        Denied(BeneficiaryStatus.Blocked, BeneficiaryStatus.Active, "super_admin").Should().NotBeNull(
            "even platform administration does not clear a fraud case — a case review does");
        Denied(BeneficiaryStatus.Blocked, BeneficiaryStatus.Active, "medical_director").Should().BeNull();
    }

    [Fact]
    public void The_denial_names_who_could_so_the_refusal_is_actionable()
    {
        Denied(BeneficiaryStatus.Blocked, BeneficiaryStatus.Active, "beneficiary_mgmt")
            .Should().Be("medical_director");
    }

    [Theory]
    [InlineData(BeneficiaryStatus.Pending, BeneficiaryStatus.Active)]
    [InlineData(BeneficiaryStatus.Active, BeneficiaryStatus.Suspended)]
    [InlineData(BeneficiaryStatus.Suspended, BeneficiaryStatus.Active)]
    [InlineData(BeneficiaryStatus.Inactive, BeneficiaryStatus.Active)]
    [InlineData(BeneficiaryStatus.Expired, BeneficiaryStatus.Active)]
    public void Routine_desk_transitions_stay_open_to_the_desk(BeneficiaryStatus from, BeneficiaryStatus to)
    {
        // The other Actor cells describe workflow, not control — several name non-role actors ("System
        // (timer)", "policy-service") — and locking them would break the portal's own status screen.
        Denied(from, to, "beneficiary_mgmt").Should().BeNull();
    }
}
