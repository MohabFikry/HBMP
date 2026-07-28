using FluentAssertions;
using Mersal.Auth;

namespace Mersal.Authz.Tests;

/// <summary>
/// 21.3 — fail-closed branch reach (design 40 §3).
///
/// The failure this guards against is specific and quiet: an EMPTY branch predicate does not mean "no
/// branches", it means "every branch in the tenant". So a caller who is supposed to see one branch's
/// worklist sees the whole organisation's — and only when resolution has already gone wrong, which is
/// exactly when nobody is looking.
/// </summary>
public class BranchSentinelTests
{
    private static readonly Guid BranchA = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid BranchB = new("11111111-0000-0000-0000-000000000002");
    private const string Tenant = "t-1";

    /// <summary>A branch-scoped caller (roles drive the mode, per design 37 §3).</summary>
    private static HbmpPrincipal Reception() => new()
    {
        Subject = "u1", TenantId = Tenant, Roles = new HashSet<string>(["reception"], StringComparer.Ordinal),
        Scopes = new HashSet<string>(StringComparer.Ordinal),
    };

    /// <summary>Rows across two branches — the "N > 0" side of the negation below.</summary>
    private static (string Tenant, Guid Branch)[] Dataset() =>
    [
        (Tenant, BranchA), (Tenant, BranchA), (Tenant, BranchB), (Tenant, BranchB), (Tenant, BranchB),
    ];

    private static int Visible(RowScope scope) =>
        Dataset().Count(r => scope.Allows(r.Tenant, rowBranchId: r.Branch));

    [Fact]
    public void THE_fail_closed_test_unresolvable_reach_returns_zero_rows()
    {
        // Resolution failed entirely: branch-scoped caller, no active branch.
        var broken = new BranchContext(ActiveBranchId: null, new HashSet<Guid>(), IsBranchUnrestricted: false);
        var scope = RowScope.For(Reception()).WithBranchScope(ScopeMode.BranchScoped, broken);

        Visible(scope).Should().Be(0, "an unresolvable branch context must match nothing");
        scope.BranchIds.Should().Contain(RowScope.NoBranchSentinel);
        scope.BranchUnrestricted.Should().BeFalse("unrestricted is the tenant-wide leak this test exists to prevent");
    }

    [Fact]
    public void AND_THE_NEGATION_an_empty_predicate_would_have_exposed_the_whole_tenant()
    {
        // Without this half the test above is a tautology: a dataset that happens to be empty, or a scope
        // that filters on something else entirely, would also "return zero rows" and the suite would go on
        // passing after the sentinel was removed. This asserts the rows ARE there to be leaked.
        var noBranchNarrowing = RowScope.For(Reception()) with { BranchIds = null, BranchUnrestricted = true };

        Visible(noBranchNarrowing).Should().Be(5,
            "the dataset must contain rows that an empty branch predicate would return — otherwise the " +
            "fail-closed assertion proves nothing");
    }

    [Fact]
    public void A_resolved_branch_narrows_to_exactly_that_branch()
    {
        var ctx = new BranchContext(BranchA, new HashSet<Guid> { BranchA, BranchB }, IsBranchUnrestricted: false);
        var scope = RowScope.For(Reception()).WithBranchScope(ScopeMode.BranchScoped, ctx);

        Visible(scope).Should().Be(2, "only branch A's rows are in reach");
    }

    [Fact]
    public void A_member_scoped_caller_is_not_narrowed_at_all()
    {
        // The branch dimension is a convenience for member-scoped roles, never a restriction — narrowing
        // them would break approvals and case management, which legitimately span branches.
        var ctx = new BranchContext(null, new HashSet<Guid>(), IsBranchUnrestricted: true);
        var scope = RowScope.For(Reception()).WithBranchScope(ScopeMode.MemberScoped, ctx);

        Visible(scope).Should().Be(5);
        scope.BranchUnrestricted.Should().BeTrue();
    }

    [Fact]
    public void A_row_with_no_branch_is_invisible_to_a_branch_scoped_caller()
    {
        // An unstamped row must not become universally visible. Fail closed on missing data too.
        var ctx = new BranchContext(BranchA, new HashSet<Guid> { BranchA }, IsBranchUnrestricted: false);
        var scope = RowScope.For(Reception()).WithBranchScope(ScopeMode.BranchScoped, ctx);

        scope.Allows(Tenant, rowBranchId: null).Should().BeFalse();
    }

    [Fact]
    public void The_sentinel_is_not_a_usable_branch_id()
    {
        // If a real branch ever received this id, the sentinel would grant access instead of denying it.
        // Keeping it in the reserved all-zeros-prefixed space makes that collision implausible for uuidv7
        // keys, and this test states the requirement so a future "tidy up the constant" cannot break it.
        RowScope.NoBranchSentinel.Should().NotBe(Guid.Empty);
        RowScope.NoBranchSentinel.ToString().Should().StartWith("00000000-0000-0000-0000-");
    }
}
