using FluentAssertions;
using Mersal.Approvals.Domain;

namespace Mersal.Approvals.Tests;

/// <summary>The canonical Authorization state machine (23-state-machines §5): only legal transitions are allowed,
/// decisions map to the right terminal state, the release-downstream set is correct, and SLA-due is priority-based.
/// No DB needed.</summary>
public class AuthorizationWorkflowTests
{
    [Theory]
    [InlineData(AuthStatus.Draft, AuthStatus.Submitted)]
    [InlineData(AuthStatus.Submitted, AuthStatus.UnderReview)]
    [InlineData(AuthStatus.Submitted, AuthStatus.EmergencyApproved)]
    [InlineData(AuthStatus.UnderReview, AuthStatus.Approved)]
    [InlineData(AuthStatus.UnderReview, AuthStatus.PartiallyApproved)]
    [InlineData(AuthStatus.UnderReview, AuthStatus.Rejected)]
    [InlineData(AuthStatus.UnderReview, AuthStatus.InfoRequested)]
    [InlineData(AuthStatus.InfoRequested, AuthStatus.UnderReview)]
    [InlineData(AuthStatus.Rejected, AuthStatus.Overridden)]
    [InlineData(AuthStatus.Approved, AuthStatus.Expired)]
    [InlineData(AuthStatus.PartiallyApproved, AuthStatus.Expired)]
    [InlineData(AuthStatus.EmergencyApproved, AuthStatus.Expired)]
    [InlineData(AuthStatus.Overridden, AuthStatus.Expired)]
    public void Legal_transitions_are_allowed(AuthStatus from, AuthStatus to) =>
        AuthorizationWorkflow.CanTransition(from, to).Should().BeTrue();

    [Theory]
    [InlineData(AuthStatus.Submitted, AuthStatus.Approved)]         // must be picked up first
    [InlineData(AuthStatus.Approved, AuthStatus.Rejected)]          // terminal decisions don't flip
    [InlineData(AuthStatus.Rejected, AuthStatus.Approved)]          // only override reopens a rejection
    [InlineData(AuthStatus.UnderReview, AuthStatus.EmergencyApproved)] // emergency is a Submitted fast-track
    [InlineData(AuthStatus.Draft, AuthStatus.UnderReview)]
    [InlineData(AuthStatus.Expired, AuthStatus.Approved)]
    public void Illegal_transitions_are_refused(AuthStatus from, AuthStatus to) =>
        AuthorizationWorkflow.CanTransition(from, to).Should().BeFalse();

    [Theory]
    [InlineData(AuthDecision.Approved, AuthStatus.Approved)]
    [InlineData(AuthDecision.PartiallyApproved, AuthStatus.PartiallyApproved)]
    [InlineData(AuthDecision.Rejected, AuthStatus.Rejected)]
    [InlineData(AuthDecision.InfoRequested, AuthStatus.InfoRequested)]
    [InlineData(AuthDecision.Overridden, AuthStatus.Overridden)]
    [InlineData(AuthDecision.EmergencyApproved, AuthStatus.EmergencyApproved)]
    public void Decision_maps_to_its_terminal_state(AuthDecision d, AuthStatus expected) =>
        AuthorizationWorkflow.ResultOf(d).Should().Be(expected);

    [Theory]
    [InlineData(AuthDecision.Approved, true)]
    [InlineData(AuthDecision.PartiallyApproved, true)]
    [InlineData(AuthDecision.EmergencyApproved, true)]
    [InlineData(AuthDecision.Overridden, true)]
    [InlineData(AuthDecision.Rejected, false)]
    [InlineData(AuthDecision.InfoRequested, false)]
    public void Release_downstream_set_is_correct(AuthDecision d, bool releases) =>
        AuthorizationWorkflow.ReleasesDownstream(d).Should().Be(releases);

    [Fact]
    public void Sla_due_is_tighter_for_higher_priority()
    {
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);
        var emergency = AuthorizationWorkflow.SlaDue(AuthPriority.Emergency, now);
        var urgent = AuthorizationWorkflow.SlaDue(AuthPriority.Urgent, now);
        var routine = AuthorizationWorkflow.SlaDue(AuthPriority.Routine, now);
        emergency.Should().BeBefore(urgent);
        urgent.Should().BeBefore(routine);
    }
}
