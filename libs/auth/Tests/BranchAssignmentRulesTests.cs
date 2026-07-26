using FluentAssertions;
using Mersal.Auth;

namespace Mersal.Auth.Tests;

/// <summary>Phase 14.2 — pure active-branch resolution rules (design 37 §2.2–2.3). Proves the permitted set
/// honours status + validity windows, the one-home resolution, and that a requested branch outside the
/// permitted set is denied (the caller then returns 403 + audits BranchScopeDenied).</summary>
public class BranchAssignmentRulesTests
{
    private static readonly Guid Maadi = Guid.NewGuid();
    private static readonly Guid Dokki = Guid.NewGuid();
    private static readonly Guid Aswan = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 7, 26);

    private static BranchAssignment Home(Guid b, DateOnly? to = null, BranchAssignmentStatus s = BranchAssignmentStatus.Active) =>
        new(b, BranchAssignmentType.Home, new DateOnly(2026, 1, 1), to, s);
    private static BranchAssignment Additional(Guid b, DateOnly? from = null, DateOnly? to = null, BranchAssignmentStatus s = BranchAssignmentStatus.Active) =>
        new(b, BranchAssignmentType.Additional, from ?? new DateOnly(2026, 1, 1), to, s);

    [Fact]
    public void Permitted_set_is_home_union_additional_filtered_to_effective()
    {
        var rows = new[] { Home(Maadi), Additional(Dokki), Additional(Aswan, s: BranchAssignmentStatus.Revoked) };
        BranchAssignmentRules.PermittedBranches(rows, Today).Should().BeEquivalentTo([Maadi, Dokki]);
    }

    [Fact]
    public void Validity_window_excludes_expired_and_not_yet_valid_assignments()
    {
        var expired = Additional(Dokki, to: new DateOnly(2026, 6, 30));
        var future = Additional(Aswan, from: new DateOnly(2026, 12, 1));
        var rows = new[] { Home(Maadi), expired, future };
        BranchAssignmentRules.PermittedBranches(rows, Today).Should().BeEquivalentTo([Maadi]);
    }

    [Fact]
    public void No_header_resolves_to_the_home_branch()
    {
        var rows = new[] { Home(Maadi), Additional(Dokki) };
        var res = BranchAssignmentRules.ResolveActiveBranch(rows, requested: null, Today);
        res.Outcome.Should().Be(BranchAssignmentRules.ResolveOutcome.ResolvedHome);
        res.BranchId.Should().Be(Maadi);
        res.Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_requested_branch_in_the_permitted_set_is_accepted()
    {
        var rows = new[] { Home(Maadi), Additional(Dokki) };
        var res = BranchAssignmentRules.ResolveActiveBranch(rows, requested: Dokki, Today);
        res.Outcome.Should().Be(BranchAssignmentRules.ResolveOutcome.ResolvedRequested);
        res.BranchId.Should().Be(Dokki);
    }

    [Fact]
    public void A_requested_branch_outside_the_permitted_set_is_denied()
    {
        var rows = new[] { Home(Maadi), Additional(Dokki) };
        var res = BranchAssignmentRules.ResolveActiveBranch(rows, requested: Aswan, Today);
        res.Outcome.Should().Be(BranchAssignmentRules.ResolveOutcome.DeniedNotPermitted);
        res.Allowed.Should().BeFalse();
        res.BranchId.Should().BeNull();
    }

    [Fact]
    public void A_revoked_home_leaves_no_home_to_default_to()
    {
        var rows = new[] { Home(Maadi, s: BranchAssignmentStatus.Revoked) };
        var res = BranchAssignmentRules.ResolveActiveBranch(rows, requested: null, Today);
        res.Outcome.Should().Be(BranchAssignmentRules.ResolveOutcome.NoHome);
        res.Allowed.Should().BeFalse();
    }
}
