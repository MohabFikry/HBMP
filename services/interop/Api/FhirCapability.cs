using System.Text.Json.Nodes;
using Mersal.Interop.Domain.Fhir;
using Mersal.Time;

namespace Mersal.Interop.Api;

/// <summary>
/// The single source of truth for which FHIR resources + interactions the façade supports. Both the endpoint
/// wiring and the <c>/metadata</c> CapabilityStatement are built from THIS registry, so the advertised
/// interactions can never drift from the implemented ones (the 13.3 conformance test asserts exactly that).
/// </summary>
public static class FhirCapability
{
    public const string FhirVersion = "4.0.1";

    /// <summary>A supported resource: read + search-type are always available; create only where safe.</summary>
    public sealed record ResourceSupport(string Name, bool CanCreate, string[] SearchParams);

    /// <summary>The nine core resources per 17-api-specifications §12. Writes only for the safe/sensible creates
    /// (ServiceRequest/referral, MedicationRequest, Observation, AllergyIntolerance) — everything else is
    /// read-only/derived and rejects POST with an OperationOutcome.</summary>
    public static readonly IReadOnlyList<ResourceSupport> Resources =
    [
        new("Patient", false, ["identifier", "name"]),
        new("Coverage", false, ["patient"]),
        new("ServiceRequest", true, ["patient"]),
        new("MedicationRequest", true, ["patient"]),
        new("DiagnosticReport", false, ["patient"]),
        new("Encounter", false, ["patient"]),
        new("Condition", false, ["patient"]),
        new("Observation", true, ["patient"]),
        new("AllergyIntolerance", true, ["patient"]),
    ];

    public static ResourceSupport? Find(string name) =>
        Resources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    /// <summary>Build the CapabilityStatement advertising exactly the implemented interactions + SMART scopes.</summary>
    public static JsonObject Statement(string baseUrl, IBusinessCalendar calendar)
    {
        var resources = new JsonArray();
        foreach (var r in Resources)
        {
            var interactions = new JsonArray(
                new JsonObject { ["code"] = "read" },
                new JsonObject { ["code"] = "search-type" });
            if (r.CanCreate) interactions.Add(new JsonObject { ["code"] = "create" });

            var searchParams = new JsonArray();
            foreach (var sp in r.SearchParams)
                searchParams.Add(new JsonObject { ["name"] = sp, ["type"] = sp == "patient" ? "reference" : "string" });

            resources.Add(new JsonObject
            {
                ["type"] = r.Name,
                ["interaction"] = interactions,
                ["searchParam"] = searchParams,
            });
        }

        return new JsonObject
        {
            ["resourceType"] = "CapabilityStatement",
            ["status"] = "active",
            ["date"] = calendar.Today().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["publisher"] = "Mersal Foundation — HBMP",
            ["kind"] = "instance",
            ["fhirVersion"] = FhirVersion,
            ["format"] = new JsonArray("application/fhir+json"),
            ["implementation"] = new JsonObject { ["description"] = "Mersal HBMP FHIR R4 façade (read-only over internal models; safe writes translate to native commands).", ["url"] = baseUrl },
            ["rest"] = new JsonArray(new JsonObject
            {
                ["mode"] = "server",
                ["documentation"] = "Every interaction reuses the SAME RBAC/ABAC + field-level minimum-necessary rules as the native /api/v1 APIs; the façade is never an authorization bypass. Every interaction is hash-chain audited.",
                ["security"] = new JsonObject
                {
                    ["service"] = new JsonArray(Fhir.CodeableConcept("http://terminology.hl7.org/CodeSystem/restful-security-service", "SMART-on-FHIR", "SMART-on-FHIR")),
                    ["description"] = "OAuth2 bearer (Phase 17 issuer). SMART-style scopes fhir:read:{Resource} / fhir:write:{Resource}; the role set per resource is the min-necessary boundary.",
                },
                ["resource"] = resources,
            }),
        };
    }
}
