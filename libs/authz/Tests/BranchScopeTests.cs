using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Authz.Tests;

/// <summary>Phase 14.3 — the BranchScope ABAC condition, RowScope branch predicate, and the role→ScopeMode
/// classifier (design 37 §3). Proves branch narrows (never widens), that member-scoped callers are
/// branch-unrestricted, and that external providers stay provider-scoped.</summary>
public class BranchScopeTests
{
    private readonly InMemoryAuditOutbox _outbox = new();
    private static readonly Guid Maadi = Guid.NewGuid();
    private static readonly Guid Dokki = Guid.NewGuid();
    private static readonly Guid Aswan = Guid.NewGuid();

    private static HbmpPrincipal Principal(params string[] roles) =>
        new() { Subject = "u-1", Roles = new HashSet<string>(roles), Scopes = new HashSet<string> { "emr:read" }, TenantId = "t0", MfaSatisfied = true };

    // A minimal bundle: a branch-scoped worklist read requiring tenant-match AND branch-scope.
    private DefaultAuthorizationEngine Engine()
    {
        var bundle = new PolicyBundle("test-14.3",
        [
            new PolicyRule
            {
                Action = "worklist:read", ResourceType = "appointment",
                Roles = new HashSet<string> { "reception", "nurse", "doctor" }, Scopes = new HashSet<string> { "emr:read" },
                RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.BranchScope],
            },
        ]);
        return new(bundle, new AuditClient(_outbox, new AuditClientContext("test"), TimeProvider.System), NullBreakGlassProvider.Instance, TimeProvider.System);
    }

    private static AuthzRequest Worklist(HbmpPrincipal p, Guid rowBranch, Guid active, params Guid[] permitted) =>
        new(p, "worklist:read", new ResourceRef
        {
            Type = "appointment", Id = "APPT-1", TenantId = "t0",
            BranchId = rowBranch, ActiveBranchId = active, PermittedBranchIds = new HashSet<Guid>(permitted),
        });

    [Fact]
    public async Task Branch_scoped_caller_allowed_on_a_row_in_the_active_branch()
    {
        var d = await Engine().EvaluateAsync(Worklist(Principal("reception"), rowBranch: Maadi, active: Maadi, Maadi, Dokki));
        d.IsAllowed.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.BranchScope);
    }

    [Fact]
    public async Task Branch_scoped_caller_denied_on_a_row_in_another_branch()
    {
        // Dokki is permitted but the active branch is Maadi → a Dokki row is out of the active worklist.
        var d = await Engine().EvaluateAsync(Worklist(Principal("nurse"), rowBranch: Dokki, active: Maadi, Maadi, Dokki));
        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Be($"abac-failed:{AbacConditions.BranchScope}");
    }

    [Fact]
    public async Task Branch_scoped_caller_denied_on_a_row_outside_the_permitted_set()
    {
        var d = await Engine().EvaluateAsync(Worklist(Principal("doctor"), rowBranch: Aswan, active: Aswan, Maadi, Dokki));
        d.IsAllowed.Should().BeFalse();
        _outbox.Events.Should().Contain(e => e.DecisionOutcome == "Deny");
    }

    [Fact]
    public void Mode_classifier_maps_roles_per_the_design_table()
    {
        BranchScopeModes.ModeFor(Principal("reception")).Should().Be(ScopeMode.BranchScoped);
        BranchScopeModes.ModeFor(Principal("doctor")).Should().Be(ScopeMode.BranchScoped);
        BranchScopeModes.ModeFor(Principal("medical_approval")).Should().Be(ScopeMode.MemberScoped);
        BranchScopeModes.ModeFor(Principal("medical_director")).Should().Be(ScopeMode.MemberScoped);
        BranchScopeModes.ModeFor(Principal("finance")).Should().Be(ScopeMode.MemberScoped);
        BranchScopeModes.ModeFor(Principal("lab_tech")).Should().Be(ScopeMode.ProviderScoped);
    }

    [Fact]
    public void RowScope_branch_narrows_for_branch_scoped_and_is_unrestricted_for_member_scoped()
    {
        var ctx = new BranchContext(Maadi, new HashSet<Guid> { Maadi, Dokki }, IsBranchUnrestricted: false);

        var branchScoped = RowScope.For(Principal("reception")).WithBranchScope(ScopeMode.BranchScoped, ctx);
        branchScoped.Allows("t0", rowBranchId: Maadi).Should().BeTrue();
        branchScoped.Allows("t0", rowBranchId: Dokki).Should().BeFalse("only the active branch is in scope");

        var memberScoped = RowScope.For(Principal("finance")).WithBranchScope(ScopeMode.MemberScoped, ctx);
        memberScoped.Allows("t0", rowBranchId: Aswan).Should().BeTrue("member-scoped spans all branches");
    }
}
