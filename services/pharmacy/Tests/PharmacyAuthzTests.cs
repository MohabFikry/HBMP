using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Pharmacy.Tests;

/// <summary>Authorization proof for prescribing / referral (US-033/US-034 / phase-4 guardrail): a treating doctor
/// may prescribe and refer; a non-treating prescriber is denied and audited; a pharmacist cannot prescribe
/// (no rule → default-deny). Min-necessary is preserved — no rule grants investigation results to pharmacy.</summary>
public class PharmacyAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(PharmacyPolicies.Bundle(), new AuditClient(_outbox, new AuditClientContext("pharmacy-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Resource(string type, bool treating) => new()
    {
        Type = type, TenantId = "t0", BeneficiaryId = "BEN-1",
        TreatingBeneficiaryIds = treating ? new HashSet<string> { "BEN-1" } : new HashSet<string>(),
    };

    [Fact]
    public async Task Treating_doctor_may_prescribe()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "rx:write"), PharmacyPolicies.RxCreate, Resource("prescription", treating: true)));
        d.IsAllowed.Should().BeTrue();
        d.SatisfiedConditions.Should().Contain(AbacConditions.TreatingRelationship);
    }

    [Fact]
    public async Task Treating_doctor_may_refer()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "referral:write"), PharmacyPolicies.ReferralCreate, Resource("referral", treating: true)));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Non_treating_prescriber_is_denied_and_audited()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "rx:write"), PharmacyPolicies.RxCreate, Resource("prescription", treating: false)));
        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Contain("treating-relationship");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task Pharmacist_cannot_prescribe()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("pharmacist", "rx:write"), PharmacyPolicies.RxCreate, Resource("prescription", treating: true)));
        d.IsAllowed.Should().BeFalse();   // no create rule for pharmacist → default-deny
    }
}
