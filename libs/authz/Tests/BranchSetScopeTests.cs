using FluentAssertions;
using Mersal.Auth;

namespace Mersal.Authz.Tests;

/// <summary>
/// 25.1 — the third reach mode (design 42 §1, ADR-0029).
///
/// A clinics manager supervises all six clinics at once, and neither existing mode expresses that:
/// BranchScoped makes them switch branches one at a time, and MemberScoped is an ungoverned "everything"
/// with no grant behind it. BranchSetScoped is `branch_id ∈ PermittedBranchIds`, resolved from the same
/// assignment rows as everyone else's reach.
///
/// The fail-closed behaviour is the part that must not regress: an unresolvable SET must match zero rows,
/// exactly as an unresolvable single branch does. Every zero-row assertion here is paired with a negation
/// proving the rows were there to be leaked.
/// </summary>
public class BranchSetScopeTests
{
    private const string Tenant = "t-1";

    private static readonly Guid Aswan = new("22222222-0000-0000-0000-00000000000a");
    private static readonly Guid Alexandria = new("22222222-0000-0000-0000-00000000000b");
    private static readonly Guid October = new("22222222-0000-0000-0000-00000000000c");
    private static readonly Guid Maadi = new("22222222-0000-0000-0000-00000000000d");
    private static readonly Guid Dokki = new("22222222-0000-0000-0000-00000000000e");
    private static readonly Guid NasrCity = new("22222222-0000-0000-0000-00000000000f");

    private static readonly Guid[] AllSix = [Aswan, Alexandria, October, Maadi, Dokki, NasrCity];

    private static HbmpPrincipal Principal(string role) => new()
    {
        Subject = "u1", TenantId = Tenant, Roles = new HashSet<string>([role], StringComparer.Ordinal),
        Scopes = new HashSet<string>(StringComparer.Ordinal),
    };

    /// <summary>Two rows per clinic, twelve in all — so "sees six branches" is a count, not a coincidence.</summary>
    private static (string Tenant, Guid Branch)[] Dataset() =>
        [.. AllSix.SelectMany(b => new[] { (Tenant, b), (Tenant, b) })];

    private static int Visible(RowScope scope) =>
        Dataset().Count(r => scope.Allows(r.Tenant, rowBranchId: r.Branch));

    private static BranchContext Context(Guid? active, params Guid[] permitted) =>
        new(active, new HashSet<Guid>(permitted), IsBranchUnrestricted: false);

    // ---- mode classification ----------------------------------------------------------------------------

    [Fact]
    public void The_clinics_manager_is_set_scoped_and_the_coordinator_is_branch_scoped()
    {
        BranchScopeModes.ModeFor(Principal("clinics_manager")).Should().Be(ScopeMode.BranchSetScoped);
        BranchScopeModes.ModeFor(Principal("branch_coordinator")).Should().Be(ScopeMode.BranchScoped);
    }

    [Fact]
    public void THE_ONE_THAT_MATTERS_the_clinics_manager_never_falls_through_to_MemberScoped()
    {
        // MemberScoped is UNRESTRICTED. If clinics_manager is ever dropped from BranchSetScopedRoles this is
        // where it lands, and nothing would look broken: the manager would see more, not less, and every
        // screen would work. That silence is the reason this assertion is written separately and named for
        // what it prevents rather than folded into the classification test above.
        BranchScopeModes.ModeFor(Principal("clinics_manager")).Should().NotBe(ScopeMode.MemberScoped,
            "MemberScoped is ungoverned reach with no grant behind it (design 42 §1) — reach that no " +
            "assignment produced cannot be reviewed, revoked or explained");
    }

    [Fact]
    public void The_retired_phantom_role_names_are_gone()
    {
        // branch_manager / clinic_manager were named in BranchScopedRoles and in the SPA's mirror of it, and
        // never seeded. A principal carrying one now classifies as MemberScoped, which is correct precisely
        // because no such role exists to grant it anything.
        BranchScopeModes.BranchScopedRoles.Should().NotContain("branch_manager");
        BranchScopeModes.BranchScopedRoles.Should().NotContain("clinic_manager");
        BranchScopeModes.BranchScopedRoles.Should().Contain("branch_coordinator");
    }

    [Fact]
    public void Both_reach_modes_count_as_branch_restricted()
    {
        // The call sites that ask "is this caller branch-restricted?" must say yes to both. Asking
        // `== BranchScoped` is the bug that would leave a set-scoped caller unrestricted.
        BranchScopeModes.IsBranchRestricted(ScopeMode.BranchScoped).Should().BeTrue();
        BranchScopeModes.IsBranchRestricted(ScopeMode.BranchSetScoped).Should().BeTrue();
        BranchScopeModes.IsBranchRestricted(ScopeMode.MemberScoped).Should().BeFalse();
        BranchScopeModes.IsBranchRestricted(ScopeMode.ProviderScoped).Should().BeFalse();
    }

    // ---- the row predicate ------------------------------------------------------------------------------

    [Fact]
    public void A_manager_reads_all_six_branches_in_one_request()
    {
        var scope = RowScope.For(Principal("clinics_manager"))
            .WithBranchScope(ScopeMode.BranchSetScoped, Context(active: null, AllSix));

        Visible(scope).Should().Be(12, "all six clinics, in one request, without switching");
        scope.BranchIds.Should().BeEquivalentTo(AllSix);
        scope.BranchUnrestricted.Should().BeFalse(
            "the set is a real predicate — a manager is restricted to their grants, just to more of them");
    }

    [Fact]
    public void A_coordinator_reads_exactly_one_branch()
    {
        var scope = RowScope.For(Principal("branch_coordinator"))
            .WithBranchScope(ScopeMode.BranchScoped, Context(Maadi, Maadi));

        Visible(scope).Should().Be(2, "one clinic's rows only");
        scope.BranchIds.Should().BeEquivalentTo([Maadi]);
    }

    [Fact]
    public void A_managers_active_branch_FILTERS_rather_than_defining_the_reach()
    {
        var filtered = RowScope.For(Principal("clinics_manager"))
            .WithBranchScope(ScopeMode.BranchSetScoped, Context(Dokki, AllSix));
        Visible(filtered).Should().Be(2, "the filter narrows the view to one clinic");

        // And the direction that proves it is a FILTER and not a switch: clearing it restores all six rather
        // than resolving to nothing. A supervisory worklist that empties when you clear its filter is the
        // failure this asserts against.
        var cleared = RowScope.For(Principal("clinics_manager"))
            .WithBranchScope(ScopeMode.BranchSetScoped, Context(active: null, AllSix));
        Visible(cleared).Should().Be(12);
    }

    [Fact]
    public void A_manager_whose_reach_is_partial_sees_only_that_part()
    {
        // Reach is grant-derived, not role-derived (design 42 §7 rule 2). A "clinics manager" holding three
        // assignments reaches three clinics — the role name grants nothing by itself.
        var scope = RowScope.For(Principal("clinics_manager"))
            .WithBranchScope(ScopeMode.BranchSetScoped, Context(active: null, Aswan, Maadi, Dokki));

        Visible(scope).Should().Be(6);
        scope.BranchIds.Should().BeEquivalentTo([Aswan, Maadi, Dokki]);
    }

    // ---- fail-closed ------------------------------------------------------------------------------------

    [Fact]
    public void THE_fail_closed_test_an_unresolvable_SET_returns_zero_rows()
    {
        var broken = Context(active: null);   // set-scoped, and the set did not resolve
        var scope = RowScope.For(Principal("clinics_manager")).WithBranchScope(ScopeMode.BranchSetScoped, broken);

        Visible(scope).Should().Be(0, "an unresolvable set must match nothing, exactly as a single branch does");
        scope.BranchIds.Should().Contain(RowScope.NoBranchSentinel);
        scope.BranchUnrestricted.Should().BeFalse();
    }

    [Fact]
    public void AND_THE_NEGATION_an_empty_set_predicate_would_have_exposed_every_clinic()
    {
        // Without this half the assertion above is a tautology — an empty dataset also "returns zero rows",
        // and the suite would keep passing after the sentinel was removed.
        var unnarrowed = RowScope.For(Principal("clinics_manager")) with { BranchIds = null, BranchUnrestricted = true };

        Visible(unnarrowed).Should().Be(12,
            "the dataset must contain rows an empty branch predicate would return, or the fail-closed " +
            "assertion proves nothing");
    }

    [Fact]
    public void A_filter_outside_the_permitted_set_is_not_silently_widened_back_to_the_set()
    {
        // Belt and braces with the resolver, which already refuses such a header. Honouring it HERE would
        // turn a rejected assertion into a quiet grant of everything the caller can reach — strictly worse
        // than the request the caller actually made.
        var scope = RowScope.For(Principal("clinics_manager"))
            .WithBranchScope(ScopeMode.BranchSetScoped, Context(NasrCity, Aswan, Maadi));

        Visible(scope).Should().Be(0);
        scope.BranchIds.Should().Contain(RowScope.NoBranchSentinel);
    }

    // ---- the ABAC condition -----------------------------------------------------------------------------

    [Fact]
    public void A_managers_write_to_a_branch_they_are_not_currently_filtered_to_is_allowed()
    {
        // D4: the clinics manager has write everywhere in reach. A UI filter is not a permission boundary —
        // a supervisor who narrowed their screen to Maadi has not resigned as supervisor of Dokki.
        var r = new AuthzRequest(Principal("clinics_manager"), "update", new ResourceRef
        {
            Type = "roster_exception", TenantId = Tenant,
            BranchId = Dokki, ActiveBranchId = Maadi,
            PermittedBranchIds = new HashSet<Guid>(AllSix),
            BranchReach = ScopeMode.BranchSetScoped,
        });

        AbacConditions.InBranchScope(r).Should().BeTrue();
    }

    [Fact]
    public void A_coordinators_write_outside_their_active_branch_is_still_refused()
    {
        // The default reach mode is unchanged, and so is its meaning: for a coordinator the active branch
        // RESTRICTS. This is the assertion that proves 25.1 did not quietly relax the single-branch case.
        var r = new AuthzRequest(Principal("branch_coordinator"), "update", new ResourceRef
        {
            Type = "roster_exception", TenantId = Tenant,
            BranchId = Dokki, ActiveBranchId = Maadi,
            PermittedBranchIds = new HashSet<Guid>([Maadi, Dokki]),
            // BranchReach deliberately left at its default — every pre-25.1 call site behaves this way.
        });

        AbacConditions.InBranchScope(r).Should().BeFalse();
    }

    [Fact]
    public void Set_reach_never_relaxes_the_permitted_set_itself()
    {
        // The mode changes what the ACTIVE branch means. It must not change what the PERMITTED set means:
        // a branch outside the grants is outside the grants in either mode.
        var r = new AuthzRequest(Principal("clinics_manager"), "update", new ResourceRef
        {
            Type = "roster_exception", TenantId = Tenant,
            BranchId = NasrCity, ActiveBranchId = null,
            PermittedBranchIds = new HashSet<Guid>([Aswan, Maadi]),
            BranchReach = ScopeMode.BranchSetScoped,
        });

        AbacConditions.InBranchScope(r).Should().BeFalse();
    }

    // ---- the resolver -----------------------------------------------------------------------------------

    private sealed class Directory(PermittedBranches pb) : IBranchDirectory
    {
        public Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default) =>
            Task.FromResult(pb);
    }

    [Fact]
    public async Task A_manager_sending_no_header_resolves_to_the_whole_set()
    {
        var state = await BranchScopeResolver.ResolveAsync(
            Principal("clinics_manager"), activeBranchHeader: null,
            new Directory(new PermittedBranches(Home: Maadi, new HashSet<Guid>(AllSix))));

        state.Denied.Should().BeFalse();
        state.Context.ActiveBranchId.Should().BeNull(
            "no filter means all six — falling back to Home would open a supervisory worklist showing a " +
            "sixth of its rows, with nothing on screen to say so");
        state.Context.PermittedBranchIds.Should().BeEquivalentTo(AllSix);
        state.Context.IsBranchUnrestricted.Should().BeFalse();
    }

    [Fact]
    public async Task A_manager_sending_a_permitted_header_resolves_to_that_filter()
    {
        var state = await BranchScopeResolver.ResolveAsync(
            Principal("clinics_manager"), Dokki.ToString(),
            new Directory(new PermittedBranches(Home: Maadi, new HashSet<Guid>(AllSix))));

        state.Denied.Should().BeFalse();
        state.Context.ActiveBranchId.Should().Be(Dokki);
    }

    [Fact]
    public async Task A_manager_sending_an_out_of_reach_header_is_DENIED_not_ignored()
    {
        // The header only filters, but it is still an assertion (doc 40 §0 A2: nothing security-relevant is
        // silent). A caller asking for a branch they cannot reach has a bug or is probing, and serving them a
        // different dataset hides both.
        var state = await BranchScopeResolver.ResolveAsync(
            Principal("clinics_manager"), NasrCity.ToString(),
            new Directory(new PermittedBranches(Home: Maadi, new HashSet<Guid>([Aswan, Maadi]))));

        state.Denied.Should().BeTrue();
    }

    [Fact]
    public async Task A_manager_with_no_assignments_at_all_resolves_to_an_empty_set_and_then_to_the_sentinel()
    {
        // End to end: resolution produces nothing, and the predicate built from it matches nothing. The
        // resolver deliberately does NOT deny here — the caller is not asserting anything wrong, they simply
        // have no reach — so the sentinel is what stops the request seeing the tenant.
        var state = await BranchScopeResolver.ResolveAsync(
            Principal("clinics_manager"), activeBranchHeader: null,
            new Directory(PermittedBranches.None));

        state.Denied.Should().BeFalse();
        var scope = RowScope.For(Principal("clinics_manager"))
            .WithBranchScope(ScopeMode.BranchSetScoped, state.Context);

        Visible(scope).Should().Be(0);
        scope.BranchIds.Should().Contain(RowScope.NoBranchSentinel);
    }

    [Fact]
    public async Task The_single_branch_resolver_path_is_unchanged()
    {
        // 25.1 touched this method. A coordinator with no header must still land on Home.
        var state = await BranchScopeResolver.ResolveAsync(
            Principal("branch_coordinator"), activeBranchHeader: null,
            new Directory(new PermittedBranches(Home: Maadi, new HashSet<Guid>([Maadi, Dokki]))));

        state.Denied.Should().BeFalse();
        state.Context.ActiveBranchId.Should().Be(Maadi);
    }
}
