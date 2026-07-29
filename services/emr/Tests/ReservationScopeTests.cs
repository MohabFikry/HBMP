using FluentAssertions;
using Mersal.Auth.Authorization;

namespace Mersal.Emr.Tests;

/// <summary>
/// Reserving an appointment and admitting a patient are different powers, and this is where they were the same
/// one. emr guarded POST /appointments with appointment:write — the scope that also permits check-in and
/// no-show — so the call centre, which must never admit anyone, had a choice between powers it should not have
/// and being unable to book at all. It had the latter: every reservation ended in a bare 403 from emr AFTER
/// passing every call-centre gate, which is why the failure looked like a call-centre bug and was not one.
/// </summary>
public class ReservationScopeTests
{
    [Fact]
    public void AnyScope_builds_a_policy_naming_every_alternative()
    {
        HbmpPolicies.AnyScope("appointment:write", "appointment:reserve")
            .Should().Be("scope:appointment:write|appointment:reserve");
    }

    [Fact]
    public void A_single_scope_policy_is_unchanged()
    {
        // The any-of form must not alter how every other endpoint on the platform is named.
        HbmpPolicies.Scope("appointment:write").Should().Be("scope:appointment:write");
    }

    [Fact]
    public void Either_alternative_satisfies_the_requirement()
    {
        var req = new ScopeRequirement(new[] { "appointment:write", "appointment:reserve" }, requireMfa: false);

        req.Scopes.Should().BeEquivalentTo("appointment:write", "appointment:reserve");
        // The desk holds write; the call centre holds reserve. Both reach booking.
        req.Scopes.Should().Contain("appointment:write");
        req.Scopes.Should().Contain("appointment:reserve");
    }

    [Fact]
    public void A_single_scope_requirement_still_accepts_only_that_scope()
    {
        // Check-in and no-show stay here. If this ever widened, reservation-only would silently stop meaning
        // anything — the call centre would gain admission powers with no code change to notice.
        var req = new ScopeRequirement("appointment:write", requireMfa: false);
        req.Scopes.Should().BeEquivalentTo("appointment:write");
        req.Scopes.Should().NotContain("appointment:reserve");
    }

    [Fact]
    public void The_denial_reason_names_the_alternatives_so_a_403_is_diagnosable()
    {
        // A bare "Forbidden" with no indication of which scope was wanted is exactly what made the original
        // failure take so long to place.
        new ScopeRequirement(new[] { "a:x", "b:y" }, requireMfa: false).Scope.Should().Be("a:x|b:y");
    }

    [Fact]
    public void An_empty_alternative_list_is_rejected_rather_than_allowing_everything()
    {
        // A requirement satisfied by nothing must not be constructible: it would read as "no scope needed".
        var act = () => new ScopeRequirement(Array.Empty<string>(), requireMfa: false);
        act.Should().Throw<ArgumentException>();
    }
}
