using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Provider.Api;

namespace Mersal.Provider.Tests;

/// <summary>
/// 25.2 — a coordinator at Maadi assigning a practitioner to Dokki is 403 + audit (design 42 §2).
///
/// This is the check that makes widening the practitioner write group SAFE. The group now admits
/// `branch:practitioner:write` as well as `provider:write`; without a reach check that widening would have
/// handed every coordinator the whole network's roster while looking, in the route table, like a carefully
/// sized permission. Every assertion below is about that gap.
/// </summary>
public class BranchReachGuardTests
{
    private static readonly Guid Maadi = new("33333333-0000-0000-0000-00000000000d");
    private static readonly Guid Dokki = new("33333333-0000-0000-0000-00000000000e");
    private static readonly Guid Aswan = new("33333333-0000-0000-0000-00000000000a");

    private sealed class Accessor(HbmpPrincipal? p) : IHbmpPrincipalAccessor
    {
        public HbmpPrincipal? Principal => p;
        public HbmpPrincipal Require() => p ?? throw new InvalidOperationException();
    }

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", TenantId = "t0", MfaSatisfied = true,
        Roles = new HashSet<string>([role], StringComparer.Ordinal),
        Scopes = new HashSet<string>(scopes, StringComparer.Ordinal),
    };

    private static HbmpPrincipal Coordinator() => Principal("branch_coordinator", "branch:practitioner:write");
    private static HbmpPrincipal Manager() => Principal("clinics_manager", "branch:practitioner:write");
    private static HbmpPrincipal NetworkTeam() => Principal("network_team", "provider:read", "provider:write");

    private readonly InMemoryAuditOutbox _outbox = new();

    private BranchReachGuard Guard(HbmpPrincipal? p, Guid? active, params Guid[] permitted)
    {
        var state = new BranchScopeState
        {
            Context = new BranchContext(active, new HashSet<Guid>(permitted), IsBranchUnrestricted: false),
        };
        var audit = new AuditClient(_outbox, new AuditClientContext("provider-test"), TimeProvider.System);
        return new BranchReachGuard(new Accessor(p), state, audit);
    }

    private int Denials => _outbox.Events.Count(e => e.DecisionOutcome == "BranchReachDenied");

    // ---- the canonical case ------------------------------------------------------------------------------

    [Fact]
    public async Task THE_CASE_a_coordinator_at_Maadi_cannot_assign_a_practitioner_to_Dokki()
    {
        var guard = Guard(Coordinator(), active: Maadi, Maadi);

        var refusal = await guard.RefuseUnlessInReachAsync(Dokki, "practitioner", "p-1");

        refusal.Should().NotBeNull("assigning into a clinic you do not run must be refused");
        Denials.Should().Be(1, "a refusal is evidence whether it was a bug or a probe");
    }

    [Fact]
    public async Task AND_THE_NEGATION_the_same_coordinator_may_assign_at_their_own_branch()
    {
        // Without this the test above would pass just as well against a guard that refuses everything, and
        // the phase would ship a coordinator who cannot do the job it exists to give them.
        var guard = Guard(Coordinator(), active: Maadi, Maadi);

        (await guard.RefuseUnlessInReachAsync(Maadi, "practitioner", "p-1")).Should().BeNull();
        Denials.Should().Be(0);
    }

    [Fact]
    public async Task A_clinics_manager_may_assign_at_any_clinic_in_reach()
    {
        // D4: write everywhere. And the branch FILTER is not a permission boundary — the manager is filtered
        // to Maadi here and still administers Dokki, because a UI filter is not a resignation.
        var guard = Guard(Manager(), active: Maadi, Maadi, Dokki, Aswan);

        (await guard.RefuseUnlessInReachAsync(Dokki, "practitioner", "p-1")).Should().BeNull();
        (await guard.RefuseUnlessInReachAsync(Aswan, "practitioner", "p-1")).Should().BeNull();
        Denials.Should().Be(0);
    }

    [Fact]
    public async Task A_clinics_manager_is_still_refused_outside_their_grants()
    {
        // Reach is grant-derived, never role-derived (design 42 §7 rule 2). The role name is not the grant.
        var guard = Guard(Manager(), active: null, Maadi, Dokki);

        (await guard.RefuseUnlessInReachAsync(Aswan, "practitioner", "p-1")).Should().NotBeNull();
        Denials.Should().Be(1);
    }

    [Fact]
    public async Task The_network_team_is_unaffected_by_the_branch_dimension()
    {
        // provider:write is network-wide by definition and was never branch-narrowed. 25.2 must not change
        // that: the Network Team administers every clinic and holds no branch assignments at all.
        var guard = Guard(NetworkTeam(), active: null);

        guard.IsNetworkWide.Should().BeTrue();
        (await guard.RefuseUnlessInReachAsync(Dokki, "practitioner", "p-1")).Should().BeNull();
        Denials.Should().Be(0);
    }

    // ---- fail-closed -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_branch_caller_whose_reach_did_not_resolve_can_administer_NOTHING()
    {
        // The admin-service lookup fail-closes to an empty set. An empty PERMITTED set must mean "no clinic",
        // never "any clinic" — the same failure the RowScope sentinel exists to prevent, one layer up.
        var guard = Guard(Coordinator(), active: null);

        (await guard.RefuseUnlessInReachAsync(Maadi, "practitioner", "p-1")).Should().NotBeNull();
        (await guard.RefuseUnlessInReachAsync(Dokki, "practitioner", "p-1")).Should().NotBeNull();
    }

    // ---- edits that do not name a branch -----------------------------------------------------------------

    [Fact]
    public async Task A_coordinator_may_edit_a_practitioner_who_works_at_their_clinic()
    {
        var guard = Guard(Coordinator(), active: Maadi, Maadi);

        (await guard.RefuseUnlessServesAReachableBranchAsync(Guid.NewGuid(), [Dokki, Maadi])).Should().BeNull(
            "the doctor works at Maadi among others, and Maadi is this coordinator's clinic");
    }

    [Fact]
    public async Task A_coordinator_may_NOT_edit_a_practitioner_who_works_only_elsewhere()
    {
        var guard = Guard(Coordinator(), active: Maadi, Maadi);

        var refusal = await guard.RefuseUnlessServesAReachableBranchAsync(Guid.NewGuid(), [Dokki, Aswan]);

        refusal.Should().NotBeNull();
        Denials.Should().Be(1,
            "ONE refusal is ONE decision — auditing per branch tried would make a single 403 read as two probes");
    }

    [Fact]
    public async Task An_UNASSIGNED_practitioner_is_reachable_only_by_a_network_wide_caller()
    {
        // The tail of D3. A coordinator may CREATE a practitioner, and the next thing they must do is assign
        // them to their own clinic. Until they do, the row belongs to nobody — and "nobody's" must not
        // quietly mean "everybody's", or the create path becomes a way to edit records at will.
        var coordinator = Guard(Coordinator(), active: Maadi, Maadi);
        (await coordinator.RefuseUnlessServesAReachableBranchAsync(Guid.NewGuid(), [])).Should().NotBeNull();

        var network = Guard(NetworkTeam(), active: null);
        (await network.RefuseUnlessServesAReachableBranchAsync(Guid.NewGuid(), [])).Should().BeNull();
    }

    [Fact]
    public async Task The_refusal_carries_a_distinct_problem_type_from_a_missing_scope()
    {
        // The caller HOLDS the authority and pointed it at a clinic they do not run. Collapsing that into a
        // generic 403 would tell a coordinator their permissions are wrong when their target is.
        var guard = Guard(Coordinator(), active: Maadi, Maadi);
        var refusal = await guard.RefuseUnlessInReachAsync(Dokki, "practitioner", "p-1");

        refusal!.GetType().Name.Should().Contain("ProblemHttpResult");
        BranchReachGuard.ProblemType.Should().Be("urn:hbmp:branch-not-in-reach");
    }

    [Fact]
    public async Task Every_refusal_is_audited_at_High_severity_with_the_actor()
    {
        var guard = Guard(Coordinator(), active: Maadi, Maadi);
        await guard.RefuseUnlessInReachAsync(Dokki, "practitioner", "p-9");

        var evt = _outbox.Events.Single(e => e.DecisionOutcome == "BranchReachDenied");
        evt.Severity.Should().Be(AuditSeverity.High);
        evt.ActorUserId.Should().Be("u-1");
        evt.TenantId.Should().Be("t0");
        evt.EntityId.Should().Be("p-9");
    }
}
