using System.Reflection;
using FluentAssertions;
using Mersal.Provider.Domain;

namespace Mersal.Provider.Tests;

/// <summary>Layer 5 of provider isolation (2b.3): the beneficiary payload that crosses to a provider carries
/// minimum-necessary fields only. This reflection test fails the build if a forbidden clinical/PII term is
/// ever added to <see cref="ProviderBoundaryPatient"/> — minimum-necessary is code, not comments.</summary>
public class MinNecessaryTests
{
    [Fact]
    public void Boundary_payload_has_no_clinical_or_pii_fields()
    {
        var props = typeof(ProviderBoundaryPatient).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var p in props)
        {
            var name = p.Name.ToLowerInvariant();
            foreach (var term in ProviderBoundaryPatient.ForbiddenTerms)
                name.Should().NotContain(term, $"provider-boundary field '{p.Name}' must be minimum-necessary");
        }
    }

    [Fact]
    public void Boundary_payload_exposes_only_the_whitelisted_fields()
    {
        var names = typeof(ProviderBoundaryPatient).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).Where(n => n != "ForbiddenTerms").ToHashSet();
        names.Should().BeEquivalentTo(["BeneficiaryRef", "MemberNo", "Initials", "Sex", "AgeYears", "OrderedServiceType", "OrderedCode"]);
    }
}
