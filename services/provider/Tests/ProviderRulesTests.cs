using FluentAssertions;
using Mersal.Provider.Domain;
using ProviderEntity = Mersal.Provider.Domain.Provider;

namespace Mersal.Provider.Tests;

public class ProviderRulesTests
{
    private static ProviderContract Contract(DateOnly from, DateOnly? to, ContractStatus status, params ContractServiceLine[] lines)
        => new() { ContractId = Guid.NewGuid(), EffectiveFrom = from, EffectiveTo = to, Status = status, ServiceLines = [.. lines] };

    private static ContractServiceLine Line(ServiceType st, CodeSystem cs, string code)
        => new() { ServiceLineId = Guid.NewGuid(), ServiceType = st, CodeSystem = cs, Code = code, AgreedPrice = 100m };

    [Theory]
    [InlineData("2026-01-01", "2026-06-30", "2026-06-30", null, true)]   // touch at boundary → overlap
    [InlineData("2026-01-01", "2026-06-30", "2026-07-01", null, false)]  // day after → no overlap
    [InlineData("2026-01-01", null, "2030-01-01", null, true)]           // open-ended swallows everything
    [InlineData("2026-01-01", "2026-06-30", "2025-01-01", "2025-12-31", false)]
    public void Overlap_detection(string aF, string? aT, string bF, string? bT, bool expected)
        => ContractRules.Overlaps(DateOnly.Parse(aF), aT is null ? null : DateOnly.Parse(aT),
            DateOnly.Parse(bF), bT is null ? null : DateOnly.Parse(bT)).Should().Be(expected);

    [Fact]
    public void InEffect_requires_active_status_and_date_in_range()
    {
        var on = new DateOnly(2026, 3, 1);
        ContractRules.InEffect(Contract(new(2026, 1, 1), new(2026, 12, 31), ContractStatus.Active), on).Should().BeTrue();
        ContractRules.InEffect(Contract(new(2026, 1, 1), new(2026, 12, 31), ContractStatus.Draft), on).Should().BeFalse();
        ContractRules.InEffect(Contract(new(2026, 6, 1), new(2026, 12, 31), ContractStatus.Active), on).Should().BeFalse();
    }

    [Fact]
    public void Capabilities_only_from_active_provider_and_in_effect_contract()
    {
        var p = new ProviderEntity
        {
            Status = ProviderStatus.Active,
            Contracts =
            [
                Contract(new(2026, 1, 1), null, ContractStatus.Active, Line(ServiceType.Lab, CodeSystem.CPT, "80053")),
                Contract(new(2020, 1, 1), new(2020, 12, 31), ContractStatus.Expired, Line(ServiceType.Lab, CodeSystem.CPT, "99999")),
            ],
        };
        var caps = CapabilityDerivation.Derive(p, new DateOnly(2026, 5, 1));
        caps.Should().ContainSingle().Which.Code.Should().Be("80053");

        p.Status = ProviderStatus.Suspended;
        CapabilityDerivation.Derive(p, new DateOnly(2026, 5, 1)).Should().BeEmpty();
    }

    [Fact]
    public void CanFulfil_is_case_insensitive_and_scoped_to_effective_contract()
    {
        var p = new ProviderEntity { Status = ProviderStatus.Active, Contracts = [Contract(new(2026, 1, 1), null, ContractStatus.Active, Line(ServiceType.Imaging, CodeSystem.CPT, "70450"))] };
        CapabilityDerivation.CanFulfil(p, CodeSystem.CPT, "70450", new DateOnly(2026, 5, 1)).Should().BeTrue();
        CapabilityDerivation.CanFulfil(p, CodeSystem.CPT, "00000", new DateOnly(2026, 5, 1)).Should().BeFalse();
    }

    [Fact]
    public void Credential_expiry_reminder_and_validity()
    {
        var on = new DateOnly(2026, 7, 1);
        var soon = new ProviderCredential { Status = CredentialStatus.Valid, ValidTo = new DateOnly(2026, 7, 20) };
        var far = new ProviderCredential { Status = CredentialStatus.Valid, ValidTo = new DateOnly(2027, 1, 1) };
        CredentialRules.ExpiryReminderDue(soon, on).Should().BeTrue();
        CredentialRules.ExpiryReminderDue(far, on).Should().BeFalse();
        CredentialRules.IsValidOn(soon, on).Should().BeTrue();
        CredentialRules.IsValidOn(new ProviderCredential { Status = CredentialStatus.Valid, ValidTo = new DateOnly(2026, 6, 1) }, on).Should().BeFalse();
    }

    [Fact]
    public void Mandatory_credentials_gate_needs_all_valid()
    {
        var on = new DateOnly(2026, 7, 1);
        var creds = new[]
        {
            new ProviderCredential { CredentialType = "license", IsMandatory = true, Status = CredentialStatus.Valid, ValidTo = new DateOnly(2027, 1, 1) },
            new ProviderCredential { CredentialType = "tax_card", IsMandatory = true, Status = CredentialStatus.Valid, ValidTo = new DateOnly(2027, 1, 1) },
        };
        CredentialRules.MandatoryCredentialsSatisfied(creds, on).Should().BeTrue();
        creds[1].ValidTo = new DateOnly(2026, 1, 1);   // expired
        CredentialRules.MandatoryCredentialsSatisfied(creds, on).Should().BeFalse();
    }
}
