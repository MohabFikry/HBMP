using System.Text.Json.Nodes;

namespace Mersal.Interop.Domain.Fhir;

/// <summary>
/// Minimal, dependency-free builders for FHIR R4 JSON (System.Text.Json.Nodes). The façade emits spec-shaped R4
/// resources without pulling a full FHIR SDK — matching the repo's minimal-dependency posture (see
/// docs/adr/0016-fhir-facade-interop.md; a Firely StructureDefinition validator can be swapped behind the 13.3
/// conformance harness later). Everything here is pure and unit-testable; no I/O, no PHI ownership.
/// </summary>
public static class Fhir
{
    /// <summary>Canonical identifier systems per HBMP identifier type (17-api-specifications §12).</summary>
    public static class IdentifierSystems
    {
        public const string NationalId = "urn:mersal:identifier:national-id";
        public const string Passport = "urn:mersal:identifier:passport";
        public const string RefugeeId = "urn:mersal:identifier:refugee-id";
        public const string UnhcrNo = "urn:mersal:identifier:unhcr-no";
        public const string MemberNo = "urn:mersal:identifier:member-no";

        public static string For(string? type) => (type ?? "").Trim() switch
        {
            "NationalID" or "NationalId" => NationalId,
            "Passport" => Passport,
            "RefugeeID" or "RefugeeId" => RefugeeId,
            "UNHCRNo" or "UnhcrNo" => UnhcrNo,
            "MemberNo" or "MemberNumber" => MemberNo,
            _ => $"urn:mersal:identifier:{(type ?? "unknown").ToLowerInvariant()}",
        };
    }

    /// <summary>Common code systems.</summary>
    public static class Systems
    {
        public const string Loinc = "http://loinc.org";
        public const string Cpt = "http://www.ama-assn.org/go/cpt";
        public const string Icd10 = "http://hl7.org/fhir/sid/icd-10";
        public const string Atc = "http://www.whocc.no/atc";
        public const string ConditionClinical = "http://terminology.hl7.org/CodeSystem/condition-clinical";
        public const string ObservationCategory = "http://terminology.hl7.org/CodeSystem/observation-category";
        public const string EncounterClass = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
        public const string AllergyCriticality = "http://hl7.org/fhir/allergy-intolerance-criticality";
        public const string Ucum = "http://unitsofmeasure.org";
    }

    public static JsonObject Resource(string resourceType, string? id = null)
    {
        var o = new JsonObject { ["resourceType"] = resourceType };
        if (!string.IsNullOrWhiteSpace(id)) o["id"] = id;
        return o;
    }

    public static JsonObject Identifier(string system, string value) =>
        new() { ["system"] = system, ["value"] = value };

    public static JsonObject Coding(string system, string code, string? display = null)
    {
        var c = new JsonObject { ["system"] = system, ["code"] = code };
        if (!string.IsNullOrWhiteSpace(display)) c["display"] = display;
        return c;
    }

    public static JsonObject CodeableConcept(string system, string code, string? display = null)
    {
        var cc = new JsonObject { ["coding"] = new JsonArray(Coding(system, code, display)) };
        if (!string.IsNullOrWhiteSpace(display)) cc["text"] = display;
        return cc;
    }

    public static JsonObject Reference(string type, string id, string? display = null)
    {
        var r = new JsonObject { ["reference"] = $"{type}/{id}" };
        if (!string.IsNullOrWhiteSpace(display)) r["display"] = display;
        return r;
    }

    public static JsonObject Quantity(decimal value, string? unit = null, string? system = null, string? code = null)
    {
        var q = new JsonObject { ["value"] = value };
        if (!string.IsNullOrWhiteSpace(unit)) q["unit"] = unit;
        if (!string.IsNullOrWhiteSpace(system)) q["system"] = system;
        if (!string.IsNullOrWhiteSpace(code)) q["code"] = code;
        return q;
    }

    /// <summary>A searchset Bundle wrapping mapped resources; each entry carries a canonical fullUrl.</summary>
    public static JsonObject SearchBundle(string baseUrl, string resourceType, IEnumerable<JsonObject> resources)
    {
        var entries = new JsonArray();
        foreach (var r in resources)
        {
            var id = r["id"]?.GetValue<string>();
            entries.Add(new JsonObject
            {
                ["fullUrl"] = id is null ? null : $"{baseUrl}/{resourceType}/{id}",
                ["resource"] = r.DeepClone(),
                ["search"] = new JsonObject { ["mode"] = "match" },
            });
        }
        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = entries.Count,
            ["entry"] = entries,
        };
    }

    /// <summary>An OperationOutcome (FHIR's error envelope).</summary>
    public static JsonObject OperationOutcome(string severity, string code, string diagnostics) =>
        new()
        {
            ["resourceType"] = "OperationOutcome",
            ["issue"] = new JsonArray(new JsonObject
            {
                ["severity"] = severity,
                ["code"] = code,
                ["diagnostics"] = diagnostics,
            }),
        };
}
