using FluentAssertions;
using Mersal.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Mersal.Authz.Tests;

/// <summary>
/// The branch predicate for a WRITE, in one place, for all three reach modes.
///
/// <para><b>What this is for.</b> <see cref="BranchQueryScope"/> exists because every branch-scoped READ on
/// the platform was written as <c>if (ActiveBranchId is { } active) …</c>, which is correct for the two modes
/// that existed when it was written and quietly wrong for <see cref="ScopeMode.BranchSetScoped"/>. The WRITE
/// path was never migrated and asked the same obsolete question — so a set-scoped caller who had not filtered
/// had <c>ActiveBranchId == null</c>, fell through the guard, and had the branch id off their own request body
/// accepted without it ever being tested against <see cref="IBranchContext.PermittedBranchIds"/>.</para>
///
/// <para>The failure is invisible in the way that matters: nothing errors, every screen works, and a clinics
/// manager granted two clinics can close, book into and cancel appointments at all six. So the assertions
/// below are written as NEGATIONS — each "allowed" case is paired with the refusal that proves the guard was
/// doing something.</para>
/// </summary>
public class BranchWriteScopeTests
{
    private static readonly Guid Maadi = new("33333333-0000-0000-0000-00000000000d");
    private static readonly Guid Dokki = new("33333333-0000-0000-0000-00000000000e");
    private static readonly Guid Aswan = new("33333333-0000-0000-0000-00000000000a");

    private static BranchContext Context(Guid? active, params Guid[] permitted) =>
        new(active, new HashSet<Guid>(permitted), IsBranchUnrestricted: false);

    private static int StatusOf(IResult? result) =>
        result is null ? 0 : ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    // ---- BranchScoped: unchanged behaviour ---------------------------------------------------------------

    [Fact]
    public void BranchScoped_a_null_request_resolves_to_the_active_branch()
    {
        var (branch, denied) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchScoped, Context(Maadi, Maadi, Dokki), requested: null);

        denied.Should().BeNull();
        branch.Should().Be(Maadi);
    }

    [Fact]
    public void BranchScoped_naming_another_branch_is_refused_even_when_it_is_permitted()
    {
        // Dokki IS in the permitted set — this caller may switch to it. They have not, and a write that
        // silently lands in a branch other than the one on screen is the surprise design 37 §3 forbids.
        var (branch, denied) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchScoped, Context(Maadi, Maadi, Dokki), requested: Dokki);

        StatusOf(denied).Should().Be(403);
        branch.Should().BeNull();
    }

    [Fact]
    public void BranchScoped_naming_the_active_branch_is_allowed()
    {
        var (branch, denied) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchScoped, Context(Maadi, Maadi, Dokki), requested: Maadi);

        denied.Should().BeNull();
        branch.Should().Be(Maadi);
    }

    [Fact]
    public void BranchScoped_with_no_resolvable_active_branch_is_refused_rather_than_trusting_the_body()
    {
        // The resolver denies this case before a handler runs, so it should be unreachable. It is asserted
        // anyway: "unreachable" is a property of today's call graph, and fail-closed must not depend on it.
        var (branch, denied) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchScoped, Context(active: null, Maadi), requested: Dokki);

        StatusOf(denied).Should().Be(403);
        branch.Should().BeNull();
    }

    // ---- BranchSetScoped: the gap this class closes ------------------------------------------------------

    [Fact]
    public void THE_ONE_THAT_MATTERS_a_set_scoped_caller_cannot_write_to_a_branch_outside_their_grants()
    {
        // A clinics manager granted Maadi and Dokki, with no filter set. Before BranchWriteScope this
        // returned (Aswan, null) — the request body's branch, accepted unexamined.
        var (branch, denied) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchSetScoped, Context(active: null, Maadi, Dokki), requested: Aswan);

        StatusOf(denied).Should().Be(403);
        branch.Should().BeNull();
    }

    [Fact]
    public void A_set_scoped_caller_may_write_to_any_branch_they_hold()
    {
        foreach (var target in new[] { Maadi, Dokki })
        {
            var (branch, denied) = BranchWriteScope.ResolveTarget(
                ScopeMode.BranchSetScoped, Context(active: null, Maadi, Dokki), requested: target);

            denied.Should().BeNull();
            branch.Should().Be(target);
        }
    }

    [Fact]
    public void A_set_scoped_caller_who_names_no_branch_is_refused_rather_than_defaulted()
    {
        // Fail-closed, and the reason is not symmetry with the coordinator. A supervisor's write with no
        // branch could plausibly mean "all six clinics", and a request that would close six clinics must say
        // so. The UI supplies a branch picker for exactly this.
        var (branch, denied) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchSetScoped, Context(active: null, Maadi, Dokki), requested: null);

        StatusOf(denied).Should().Be(400);
        branch.Should().BeNull();
    }

    [Fact]
    public void A_set_scoped_filter_narrows_the_write_to_the_filtered_branch()
    {
        // The manager has filtered to Maadi. Writing to Dokki — still in their grants — is refused while the
        // filter stands, for the same reason a coordinator cannot write outside their active branch: what is
        // on screen and what is written must be the same clinic.
        var (_, denied) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchSetScoped, Context(Maadi, Maadi, Dokki), requested: Dokki);

        StatusOf(denied).Should().Be(403);

        var (branch, allowed) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchSetScoped, Context(Maadi, Maadi, Dokki), requested: null);
        allowed.Should().BeNull();
        branch.Should().Be(Maadi);
    }

    [Fact]
    public void A_set_scoped_caller_whose_reach_did_not_resolve_can_write_nowhere()
    {
        // An empty permitted set is the sentinel case: it means "reach unresolved", never "every branch".
        var (branch, denied) = BranchWriteScope.ResolveTarget(
            ScopeMode.BranchSetScoped, Context(active: null), requested: Maadi);

        StatusOf(denied).Should().Be(403);
        branch.Should().BeNull();
    }

    // ---- Unrestricted modes: untouched ------------------------------------------------------------------

    [Fact]
    public void Member_and_provider_scoped_callers_write_wherever_they_name()
    {
        foreach (var mode in new[] { ScopeMode.MemberScoped, ScopeMode.ProviderScoped })
        {
            var (branch, denied) = BranchWriteScope.ResolveTarget(mode, BranchContext.Unrestricted, Aswan);
            denied.Should().BeNull();
            branch.Should().Be(Aswan);

            var (none, alsoAllowed) = BranchWriteScope.ResolveTarget(mode, BranchContext.Unrestricted, null);
            alsoAllowed.Should().BeNull();
            none.Should().BeNull();
        }
    }

    // ---- RefuseUnlessWritable: the guard for an EXISTING row ---------------------------------------------

    [Fact]
    public void An_existing_row_in_another_branch_is_refused_for_both_restricted_modes()
    {
        StatusOf(BranchWriteScope.RefuseUnlessWritable(
            ScopeMode.BranchScoped, Context(Maadi, Maadi, Dokki), owning: Dokki)).Should().Be(403);

        StatusOf(BranchWriteScope.RefuseUnlessWritable(
            ScopeMode.BranchSetScoped, Context(active: null, Maadi, Dokki), owning: Aswan)).Should().Be(403);
    }

    [Fact]
    public void An_existing_row_inside_reach_is_allowed_for_both_restricted_modes()
    {
        BranchWriteScope.RefuseUnlessWritable(
            ScopeMode.BranchScoped, Context(Maadi, Maadi, Dokki), owning: Maadi).Should().BeNull();

        BranchWriteScope.RefuseUnlessWritable(
            ScopeMode.BranchSetScoped, Context(active: null, Maadi, Dokki), owning: Dokki).Should().BeNull();
    }

    [Fact]
    public void A_branchless_row_is_left_to_the_endpoints_own_404()
    {
        // Pre-branch and external-provider rows carry no branch. Refusing here would turn "this record
        // predates branch scoping" into a permission error, which is a different and misleading answer.
        BranchWriteScope.RefuseUnlessWritable(
            ScopeMode.BranchScoped, Context(Maadi, Maadi), owning: null).Should().BeNull();

        BranchWriteScope.RefuseUnlessWritable(
            ScopeMode.BranchSetScoped, Context(active: null, Maadi), owning: null).Should().BeNull();
    }
}
