using FluentAssertions;
using Mersal.Auth;
using static Mersal.Auth.BranchAssignmentRules;

namespace Mersal.Auth.Tests;

/// <summary>
/// 21.3 — the active-branch precedence chain and its DUAL failure semantics (design 40 §3).
///
///   ① X-Active-Branch header  ② persisted preference  ③ home branch  ④ first accessible
///
/// The two failure modes are deliberately different, and conflating them is the mistake these tests exist
/// to prevent. An out-of-scope HEADER is a refusal: a programmatic caller named a dataset, and quietly
/// serving a different one is how a batch job writes to the wrong branch. An out-of-scope PREFERENCE is
/// skipped: a remembered UI selection is a convenience, and letting last month's cover expire should not
/// lock someone out of their own session.
/// </summary>
public class BranchPrecedenceChainTests
{
    private static readonly Guid Home = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Extra = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Foreign = new("aaaaaaaa-0000-0000-0000-000000000009");
    private static readonly DateOnly Today = new(2026, 7, 28);

    private static BranchAssignment Grant(
        Guid id, BranchAssignmentType type = BranchAssignmentType.Additional,
        DateOnly? from = null, DateOnly? to = null) =>
        new(id, type, from ?? new DateOnly(2026, 1, 1), to, BranchAssignmentStatus.Active);

    private static readonly BranchAssignment[] Standard =
    [
        Grant(Home, BranchAssignmentType.Home),
        Grant(Extra),
    ];

    // ---- ① header ---------------------------------------------------------------------------------------

    [Fact]
    public void An_in_scope_header_wins_over_everything_below_it()
    {
        var r = ResolveActiveBranch(Standard, requested: Extra, preference: Home, Today);

        r.Outcome.Should().Be(ResolveOutcome.ResolvedRequested);
        r.BranchId.Should().Be(Extra);
    }

    [Fact]
    public void An_out_of_scope_header_is_DENIED_not_quietly_redirected()
    {
        // The acceptance case: X-Active-Branch naming a branch the caller has no grant for.
        var r = ResolveActiveBranch(Standard, requested: Foreign, preference: Home, Today);

        r.Outcome.Should().Be(ResolveOutcome.DeniedNotPermitted);
        r.Allowed.Should().BeFalse();
        r.BranchId.Should().BeNull("a denied request must not carry a branch the caller could act under");
        // The active set travels with the refusal so the audit event can record what they COULD have asked
        // for — a denial with no context is one nobody can act on.
        r.Permitted.Should().BeEquivalentTo([Home, Extra]);
    }

    // ---- ② preference -----------------------------------------------------------------------------------

    [Fact]
    public void An_in_scope_preference_is_used_when_no_header_is_sent()
    {
        var r = ResolveActiveBranch(Standard, requested: null, preference: Extra, Today);

        r.Outcome.Should().Be(ResolveOutcome.ResolvedRequested);
        r.BranchId.Should().Be(Extra);
    }

    [Fact]
    public void A_stale_preference_is_skipped_and_the_request_still_succeeds()
    {
        // The acceptance case: a cookie remembering a branch whose grant has expired. The session must keep
        // working under the fallback, and the outcome must SAY it fell back so the UI can silently correct
        // its switcher rather than leaving a dead selection on screen.
        var r = ResolveActiveBranch(Standard, requested: null, preference: Foreign, Today);

        r.Allowed.Should().BeTrue();
        r.BranchId.Should().Be(Home);
        r.Outcome.Should().Be(ResolveOutcome.ResolvedAfterStalePreference);
    }

    [Fact]
    public void The_two_failure_modes_are_genuinely_different()
    {
        // Stated as one assertion because the risk is that a later refactor collapses them into a single
        // "not permitted" path — which would either start rejecting sessions over a stale cookie, or start
        // silently redirecting programmatic callers onto another branch's data.
        var viaHeader = ResolveActiveBranch(Standard, requested: Foreign, preference: null, Today);
        var viaPreference = ResolveActiveBranch(Standard, requested: null, preference: Foreign, Today);

        viaHeader.Allowed.Should().BeFalse("an explicit header names a dataset and must be honoured or refused");
        viaPreference.Allowed.Should().BeTrue("a preference is a hint and must never break the session");
    }

    // ---- ③ home and ④ first accessible ------------------------------------------------------------------

    [Fact]
    public void Home_is_used_when_no_header_and_no_preference()
    {
        ResolveActiveBranch(Standard, requested: null, preference: null, Today)
            .Should().BeEquivalentTo(new { Outcome = ResolveOutcome.ResolvedHome, BranchId = Home });
    }

    [Fact]
    public void Without_a_home_the_first_accessible_branch_is_chosen_in_a_stable_order()
    {
        // Stability matters: an unordered "first" would move someone between branches between requests, so
        // the same person would file two records against two different branches without ever choosing.
        BranchAssignment[] noHome = [Grant(Extra), Grant(Home)];

        var a = ResolveActiveBranch(noHome, null, null, Today);
        var b = ResolveActiveBranch(noHome.Reverse().ToArray(), null, null, Today);

        a.Outcome.Should().Be(ResolveOutcome.ResolvedFirstAccessible);
        a.BranchId.Should().Be(b.BranchId, "the chosen branch must not depend on row order");
    }

    [Fact]
    public void No_reachable_branch_at_all_resolves_to_nothing_so_the_caller_injects_the_sentinel()
    {
        var r = ResolveActiveBranch([], null, null, Today);

        r.Allowed.Should().BeFalse();
        r.BranchId.Should().BeNull();
        r.Permitted.Should().BeEmpty();
    }

    // ---- expiry -----------------------------------------------------------------------------------------

    [Fact]
    public void A_grant_that_ended_yesterday_is_out_of_the_active_set_today()
    {
        // The acceptance case. Expiry is judged at RESOLUTION time — no sweeper, so a missed job can never
        // leave reach switched on.
        BranchAssignment[] expired =
        [
            Grant(Home, BranchAssignmentType.Home),
            Grant(Extra, to: Today.AddDays(-1)),
        ];

        var permitted = PermittedBranches(expired, Today);
        permitted.Should().NotContain(Extra, "reads must exclude it and the switcher must stop offering it");

        ResolveActiveBranch(expired, requested: Extra, preference: null, Today)
            .Outcome.Should().Be(ResolveOutcome.DeniedNotPermitted, "writes to it must be rejected too");
    }

    [Fact]
    public void The_expiry_boundary_is_inclusive_on_the_final_day()
    {
        // A grant "until the 30th" covers the 30th. Off-by-one here silently cuts someone's cover a day
        // short, which surfaces as an inexplicable mid-shift lockout.
        BranchAssignment[] lastDay = [Grant(Extra, to: Today)];
        PermittedBranches(lastDay, Today).Should().Contain(Extra);
        PermittedBranches(lastDay, Today.AddDays(1)).Should().NotContain(Extra);
    }

    [Fact]
    public void A_grant_that_has_not_started_yet_is_not_in_the_active_set()
    {
        BranchAssignment[] future = [Grant(Extra, from: Today.AddDays(1))];
        PermittedBranches(future, Today).Should().BeEmpty();
    }
}
