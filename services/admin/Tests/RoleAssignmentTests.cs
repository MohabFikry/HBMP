using FluentAssertions;
using Mersal.Admin.Domain;

namespace Mersal.Admin.Tests;

/// <summary>Pure assignment-policy tests: unknown/duplicate roles are rejected, SoD conflicts surface the reason,
/// and T3/T4 grants get a recertification deadline while lower tiers do not.</summary>
public class RoleAssignmentTests
{
    [Fact]
    public void Unknown_role_is_rejected()
    {
        var e = RoleAssignment.Evaluate([], "wizard");
        e.Allowed.Should().BeFalse();
        e.ReasonCode.Should().Be("unknown-role");
    }

    [Fact]
    public void Duplicate_active_grant_is_rejected()
    {
        var e = RoleAssignment.Evaluate(["doctor"], "doctor");
        e.Allowed.Should().BeFalse();
        e.ReasonCode.Should().Be("already-granted");
    }

    [Fact]
    public void Sod_conflict_is_rejected_with_reason()
    {
        var e = RoleAssignment.Evaluate(["doctor"], "medical_approval");
        e.Allowed.Should().BeFalse();
        e.ReasonCode.Should().Be("sod-conflict");
        e.Violations.Should().NotBeEmpty();
    }

    [Fact]
    public void Clean_grant_is_allowed()
    {
        RoleAssignment.Evaluate(["reception"], "doctor").Allowed.Should().BeTrue();
    }

    [Fact]
    public void T3_and_T4_grants_get_a_quarterly_review_deadline_lower_tiers_do_not()
    {
        var now = DateTimeOffset.UtcNow;
        RoleAssignment.ReviewDueAt(SensitivityTier.T3, now).Should().Be(now.AddDays(90));
        RoleAssignment.ReviewDueAt(SensitivityTier.T4, now).Should().Be(now.AddDays(90));
        RoleAssignment.ReviewDueAt(SensitivityTier.T1, now).Should().BeNull();
        RoleCatalog.TierOf("doctor").Should().Be(SensitivityTier.T3);
        RoleCatalog.TierOf("super_admin").Should().Be(SensitivityTier.T4);
    }
}
