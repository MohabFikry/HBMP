using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Approvals.Tests;

/// <summary>Authorization proof for the approvals surface (US-060 min-necessary): the Medical Approval team and the
/// Medical Director may open the clinical review view (tenant-scoped oversight, no treating relationship); finance
/// and reception are default-denied and the deny is audited; only the Director may emergency-approve / override.
/// Exercised against the real engine over <see cref="ApprovalsPolicies"/>.</summary>
public class ApprovalsAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(ApprovalsPolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("approvals-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Auth(string id = "a-1") =>
        new() { Type = ApprovalsPolicies.Resource, Id = id, TenantId = "t0" };

    [Fact]
    public async Task Medical_approval_reviewer_may_open_the_clinical_review()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_approval", "auth:review"), ApprovalsPolicies.Review, Auth(), "PUR"));
        d.IsAllowed.Should().BeTrue();
        // Sensitive allow → audited (PHI-read oversight).
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Allow" && e.Purpose == "PUR");
    }

    [Fact]
    public async Task Director_may_open_the_clinical_review()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "auth:review"), ApprovalsPolicies.Review, Auth(), "PUR"));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Finance_cannot_open_the_clinical_review()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("finance", "auth:review"), ApprovalsPolicies.Review, Auth(), "PUR"));
        d.IsAllowed.Should().BeFalse();
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    [Fact]
    public async Task Reception_cannot_open_the_clinical_review()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("reception", "auth:review"), ApprovalsPolicies.Review, Auth(), "PUR"));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Review_requires_the_review_scope_even_for_a_reviewer()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_approval", "auth:read"), ApprovalsPolicies.Review, Auth(), "PUR"));
        d.IsAllowed.Should().BeFalse();     // has the role but not the auth:review scope
        d.ReasonCode.Should().Be("missing-scope");
    }

    [Fact]
    public async Task Reviewer_may_read_the_worklist_and_assign()
    {
        var list = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_approval", "auth:read"), ApprovalsPolicies.List, Auth()));
        list.IsAllowed.Should().BeTrue();

        var assign = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_approval", "auth:review"), ApprovalsPolicies.Assign, Auth()));
        assign.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Only_the_director_may_emergency_approve_or_override()
    {
        var reviewerEmergency = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_approval", "auth:emergency"), ApprovalsPolicies.Emergency, Auth()));
        reviewerEmergency.IsAllowed.Should().BeFalse();   // reviewer is not a Director

        var directorEmergency = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "auth:emergency"), ApprovalsPolicies.Emergency, Auth()));
        directorEmergency.IsAllowed.Should().BeTrue();

        var directorOverride = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "auth:override"), ApprovalsPolicies.Override, Auth()));
        directorOverride.IsAllowed.Should().BeTrue();
    }

    // ---- ADR-0035 §5: authoring the engine's rules -------------------------------------------------

    [Fact]
    public async Task The_supervisor_may_author_engine_rules()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "auth:configure"), ApprovalsPolicies.Configure, Auth(), "PUR"));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_reviewer_may_NOT_author_the_rules_that_route_their_own_work()
    {
        // The whole reason `auth:configure` is separate from `auth:decide`. A reviewer who could edit the rule
        // routing their own queue could route work away from themselves, and the change would look like
        // ordinary configuration rather than like avoiding a decision.
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_approval", "auth:decide", "auth:review"),
            ApprovalsPolicies.Configure, Auth(), "PUR"));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Holding_the_scope_without_the_role_is_still_refused()
    {
        // Scope alone is not authority. A token minted with `auth:configure` for a role the rule does not name
        // must still be refused — otherwise the role list in the policy is decoration.
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("finance", "auth:configure"), ApprovalsPolicies.Configure, Auth(), "PUR"));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Authoring_a_rule_is_audited_as_a_sensitive_act()
    {
        // A rule shapes a thousand cases. Who changed it, and when, has to be recoverable.
        await Engine().EvaluateAsync(new AuthzRequest(
            Principal("medical_director", "auth:configure"), ApprovalsPolicies.Configure, Auth(), "PUR"));
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Allow");
    }
}
