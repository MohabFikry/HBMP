using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Mersal.Authz.Tests;

/// <summary>
/// 21.5 — the governance guards: A6 revocation degradation and A2 switch auditing (design 40 §6).
///
/// Both encode a decision about which way to fail, and in both the two directions are opposite on purpose.
/// A test suite that only checked the happy path would let either be quietly reversed.
/// </summary>
public class GovernanceGuardTests
{
    // ---- A6: degradation ---------------------------------------------------------------------------------

    [Fact]
    public void A_refresh_check_fails_OPEN_when_the_revocation_store_is_down()
    {
        // Refusing every refresh during an infrastructure blip logs out every clinician on the platform
        // mid-shift. The exposure from proceeding is bounded by the access-token TTL; the outage is not.
        RevocationDegradation.OnStoreFailure(RevocationOperation.RefreshCheck)
            .Should().Be(DegradationAction.FailOpen);
    }

    [Fact]
    public void An_explicit_revoke_fails_CLOSED_when_the_revocation_store_is_down()
    {
        // An operator revoking a session is acting on an off-boarding or a suspected compromise. Reporting
        // success for a revocation that was never persisted is worse than any outage: they believe the
        // access is gone, close the incident, and stop looking.
        RevocationDegradation.OnStoreFailure(RevocationOperation.ExplicitRevoke)
            .Should().Be(DegradationAction.FailClosed);
    }

    [Fact]
    public void THE_pairing_the_two_directions_are_opposite()
    {
        // Stated as one assertion because the realistic regression is someone "tidying up" the switch into
        // a single branch — and either single answer is wrong in one of the two situations.
        RevocationDegradation.OnStoreFailure(RevocationOperation.RefreshCheck)
            .Should().NotBe(RevocationDegradation.OnStoreFailure(RevocationOperation.ExplicitRevoke));
    }

    [Fact]
    public void An_unrecognised_operation_takes_the_safe_direction()
    {
        // A new operation added later must not inherit the permissive branch by default.
        RevocationDegradation.OnStoreFailure((RevocationOperation)99)
            .Should().Be(DegradationAction.FailClosed);
    }

    [Fact]
    public void The_exposure_bound_is_the_access_token_ttl()
    {
        // The runbook has to be able to answer "how long can a revoked session survive". "We are not sure"
        // is not an answer anyone can act on during an incident.
        RevocationDegradation.ExposureBound(TimeSpan.FromMinutes(5)).Should().Be(TimeSpan.FromMinutes(5));
    }

    // ---- A2: nothing silent ------------------------------------------------------------------------------

    [Fact]
    public void Switching_to_a_membership_you_hold_is_allowed_and_audited()
    {
        var d = MembershipSwitch.Decide("m-1", targetIsOwnMembership: true, isPlatformAdmin: false, crossTenant: true);

        d.Allowed.Should().BeTrue();
        d.AuditEvent.Should().Be(MembershipSwitch.Switched);
        d.AdministrativeOnly.Should().BeFalse();
    }

    [Fact]
    public void THE_acceptance_case_a_cross_tenant_switch_without_a_membership_is_denied_and_audited()
    {
        var d = MembershipSwitch.Decide("m-1", targetIsOwnMembership: false, isPlatformAdmin: false, crossTenant: true);

        d.Allowed.Should().BeFalse();
        d.AuditEvent.Should().Be(MembershipSwitch.TenantSwitchDenied);
        d.Reason.Should().Be("cross-tenant-without-membership");
    }

    [Fact]
    public void EVERY_outcome_produces_an_audit_event_including_the_refusals()
    {
        // A2 — nothing silent. An ignored attempt leaves no trace, and afterwards "nobody tried" and
        // "somebody tried and we dropped it" are indistinguishable — which is exactly the signal an
        // investigation needs.
        var outcomes = new[]
        {
            MembershipSwitch.Decide("m", true, false, false),
            MembershipSwitch.Decide("m", true, false, true),
            MembershipSwitch.Decide("m", false, false, false),
            MembershipSwitch.Decide("m", false, false, true),
            MembershipSwitch.Decide("m", false, true, true),
            MembershipSwitch.Decide(null, false, false, true),
        };

        outcomes.Should().OnlyContain(o => !string.IsNullOrEmpty(o.AuditEvent));
    }

    [Fact]
    public void A_platform_admin_may_switch_without_a_membership_but_only_ADMINISTRATIVELY()
    {
        // A1 again, at the switching surface. The flag is carried separately from the allow precisely so a
        // caller cannot read "may administer this tenant" as "may read this tenant's patients".
        var d = MembershipSwitch.Decide("m-1", targetIsOwnMembership: false, isPlatformAdmin: true, crossTenant: true);

        d.Allowed.Should().BeTrue();
        d.AdministrativeOnly.Should().BeTrue(
            "a platform admin without a membership reaches administration keys only — never clinical data");
    }

    [Fact]
    public void Holding_the_membership_beats_the_platform_admin_path()
    {
        // Someone who genuinely holds the membership acts under it normally, not as an administrator — so
        // their reach is their roles', not the administrative subset.
        MembershipSwitch.Decide("m-1", targetIsOwnMembership: true, isPlatformAdmin: true, crossTenant: true)
            .AdministrativeOnly.Should().BeFalse();
    }

    [Fact]
    public void The_denial_is_a_distinct_problem_type_carrying_its_reason()
    {
        var result = MembershipSwitch.Denied("cross-tenant-without-membership");
        result.Should().BeAssignableTo<ProblemHttpResult>();

        ProblemDetails p = ((ProblemHttpResult)result).ProblemDetails;
        p.Status.Should().Be(403);
        p.Type.Should().Be(MembershipSwitch.DeniedType);
        p.Extensions["reason"].Should().Be("cross-tenant-without-membership");

        // Must not leak whether the target tenant exists — "no such tenant" and "not yours" are different
        // facts, and answering the second with the first is an enumeration oracle.
        p.Detail.Should().NotContainEquivalentOf("does not exist");
    }
}
