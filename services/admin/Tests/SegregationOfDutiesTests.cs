using FluentAssertions;
using Mersal.Authz;

namespace Mersal.Admin.Tests;

/// <summary>Proves the Segregation-of-Duties matrix (10-role-matrix §7) at assignment time: EVERY named incompatible
/// pair is blocked when a user would hold both, no self-elevation path exists (Org Admin → Super Admin, Provider
/// Admin → clinical), and a clean grant is allowed. Pure engine — no DB.</summary>
public class SegregationOfDutiesTests
{
    // Every conflict from the §7 matrix, expressed as (alreadyHeld, proposed) → must conflict.
    public static IEnumerable<object[]> ConflictPairs() => new[]
    {
        new object[] { "doctor", "medical_approval" },
        new object[] { "doctor", "medical_director" },
        new object[] { "medical_approval", "doctor" },              // order-independent
        new object[] { "finance:payment_initiate", "finance:payment_release" },
        new object[] { "beneficiary_mgmt:create_merge", "beneficiary_mgmt:merge_approve" },
        new object[] { "org_admin", "super_admin" },
        new object[] { "super_admin", "org_admin" },
        new object[] { "network_team", "finance:payment_release" },
        new object[] { "provider_admin", "doctor" },
        new object[] { "provider_admin", "nurse" },
        new object[] { "provider_admin", "pharmacist" },
        new object[] { "provider_admin", "lab_tech" },
        new object[] { "provider_admin", "imaging_tech" },
        new object[] { "claims:submitter", "claims_officer" },
        new object[] { "claims:submitter", "claims_reviewer" },
        new object[] { "claims_officer", "claims_reviewer" },
        new object[] { "claims_officer", "finance:payment_release" },
        new object[] { "claims_reviewer", "finance:payment_release" },
        new object[] { "doctor", "claims_officer" },                // provider-affiliated deciding claims
        new object[] { "pharmacist", "claims_reviewer" },
        new object[] { "claims:settlement_issuer", "finance:payment_initiate" },
        new object[] { "claims:settlement_issuer", "finance:payment_release" },
    };

    [Theory]
    [MemberData(nameof(ConflictPairs))]
    public void Every_incompatible_pair_is_blocked(string held, string proposed)
    {
        var violations = SegregationOfDuties.Evaluate([held], [proposed]);
        violations.Should().NotBeEmpty($"holding '{held}' must block a '{proposed}' grant (SoD §7)");
        violations[0].Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Org_admin_cannot_be_granted_super_admin_no_privilege_escalation()
    {
        var v = SegregationOfDuties.Evaluate(["org_admin"], ["super_admin"]);
        v.Should().Contain(x => x.Reason.Contains("privilege escalation"));
    }

    [Fact]
    public void A_coarse_finance_grant_covers_both_payment_halves()
    {
        // Holding coarse `finance` implies both initiate+release, so a network_team grant on top still conflicts.
        SegregationOfDuties.Conflicts(["finance"], ["network_team"]).Should().BeTrue();
    }

    [Fact]
    public void A_clean_grant_is_allowed()
    {
        SegregationOfDuties.Evaluate(["reception"], ["call_center"]).Should().BeEmpty();
        SegregationOfDuties.Conflicts(["doctor"], ["nurse"]).Should().BeFalse();
    }

    [Fact]
    public void A_preexisting_conflict_not_introduced_by_this_grant_is_not_reported()
    {
        // If the two conflicting tokens were both already held, proposing an unrelated third role does not
        // re-flag the pre-existing pair (we only report what THIS grant introduces).
        var v = SegregationOfDuties.Evaluate(["org_admin", "super_admin"], ["reception"]);
        v.Should().BeEmpty();
    }
}
