using FluentAssertions;
using Mersal.Interop.Api;

namespace Mersal.Interop.Tests;

/// <summary>The CapabilityStatement must advertise EXACTLY the implemented interactions — the 13.3 conformance
/// check leans on this being generated from the same registry the endpoints are wired from.</summary>
public class CapabilityTests
{
    [Fact]
    public void Statement_lists_nine_resources_with_read_and_search()
    {
        var stmt = FhirCapability.Statement("https://x/fhir/r4");
        stmt["resourceType"]!.GetValue<string>().Should().Be("CapabilityStatement");
        stmt["fhirVersion"]!.GetValue<string>().Should().Be("4.0.1");
        var resources = stmt["rest"]!.AsArray()[0]!["resource"]!.AsArray();
        resources.Should().HaveCount(9);
    }

    [Fact]
    public void Only_the_safe_creates_advertise_create()
    {
        var stmt = FhirCapability.Statement("https://x/fhir/r4");
        var resources = stmt["rest"]!.AsArray()[0]!["resource"]!.AsArray();

        bool CanCreate(string type) => resources.First(r => r!["type"]!.GetValue<string>() == type)!["interaction"]!
            .AsArray().Any(i => i!["code"]!.GetValue<string>() == "create");

        CanCreate("ServiceRequest").Should().BeTrue();
        CanCreate("MedicationRequest").Should().BeTrue();
        CanCreate("Observation").Should().BeTrue();
        CanCreate("AllergyIntolerance").Should().BeTrue();
        CanCreate("DiagnosticReport").Should().BeFalse();
        CanCreate("Condition").Should().BeFalse();
        CanCreate("Patient").Should().BeFalse();
        CanCreate("Coverage").Should().BeFalse();
        CanCreate("Encounter").Should().BeFalse();
    }
}
