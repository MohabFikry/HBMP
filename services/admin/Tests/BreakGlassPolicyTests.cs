using FluentAssertions;
using Mersal.Admin.Domain;

namespace Mersal.Admin.Tests;

/// <summary>Pure break-glass rule tests (18-security-model §11): dual control (no self-approval), the scope check
/// (no field-deny bypass beyond the named resource types/ids, fail-closed on empty scope), and the activation
/// window.</summary>
public class BreakGlassPolicyTests
{
    private static BreakGlassGrantRecord Grant(string requester) => new() { RequesterUserId = requester };

    [Fact]
    public void The_requester_cannot_approve_their_own_grant()
    {
        BreakGlassPolicy.CanApprove(Grant("alice"), "alice").Should().BeFalse();
        BreakGlassPolicy.CanApprove(Grant("alice"), "bob").Should().BeTrue();
    }

    [Fact]
    public void Access_is_widened_only_for_a_scoped_resource_type()
    {
        string[] types = ["encounter"];
        string[] ids = [];
        BreakGlassPolicy.InScope(types, ids, "encounter", "e-1").Should().BeTrue();
        BreakGlassPolicy.InScope(types, ids, "prescription", "rx-1").Should().BeFalse(); // out of scope
    }

    [Fact]
    public void An_id_scoped_grant_only_covers_the_named_ids()
    {
        string[] types = ["encounter"];
        string[] ids = ["e-1"];
        BreakGlassPolicy.InScope(types, ids, "encounter", "e-1").Should().BeTrue();
        BreakGlassPolicy.InScope(types, ids, "encounter", "e-2").Should().BeFalse();
    }

    [Fact]
    public void An_empty_scope_widens_nothing_fail_closed()
    {
        BreakGlassPolicy.InScope([], [], "encounter", "e-1").Should().BeFalse();
    }

    [Fact]
    public void The_window_is_bounded_and_starts_now()
    {
        var now = DateTimeOffset.UtcNow;
        var (nb, exp) = BreakGlassPolicy.Window(now, 60);
        nb.Should().Be(now);
        exp.Should().Be(now.AddMinutes(60));
        // clamped to a max of 4h.
        BreakGlassPolicy.Window(now, 9999).ExpiresAt.Should().Be(now.AddMinutes(240));
    }

    [Fact]
    public void A_grant_is_only_live_while_active_and_within_the_window()
    {
        var now = DateTimeOffset.UtcNow;
        var g = new BreakGlassGrantRecord
        {
            Status = BreakGlassStatus.Active, NotBefore = now.AddMinutes(-1), ExpiresAt = now.AddMinutes(59),
        };
        g.IsActiveAt(now).Should().BeTrue();
        g.IsActiveAt(now.AddHours(2)).Should().BeFalse();     // past window
        new BreakGlassGrantRecord { Status = BreakGlassStatus.Approved }.IsActiveAt(now).Should().BeFalse();
    }
}
