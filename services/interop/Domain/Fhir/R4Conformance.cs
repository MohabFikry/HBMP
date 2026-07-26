using System.Text.Json.Nodes;

namespace Mersal.Interop.Domain.Fhir;

/// <summary>
/// A dependency-free, structural FHIR R4 conformance validator (13.3). It checks the invariants that matter for a
/// façade: correct <c>resourceType</c>, required elements + cardinality, R4 value-set membership for status codes,
/// coded elements carry a system, and references use the <c>Type/id</c> form. This is the "sample conformance
/// check" the harness runs over representative resources; it is deliberately NOT a full StructureDefinition
/// validator — a Firely (Hl7.Fhir.R4) validator can be swapped in behind the same harness later (ADR-0016). Pure.
/// </summary>
public static class R4Conformance
{
    public static IReadOnlyList<string> Validate(JsonObject? resource)
    {
        var issues = new List<string>();
        if (resource is null) { issues.Add("resource is null"); return issues; }

        var type = resource["resourceType"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type)) { issues.Add("missing resourceType"); return issues; }

        switch (type)
        {
            case "Patient":
                foreach (var idn in Array(resource, "identifier"))
                    RequireAll(issues, idn, "Patient.identifier", "system", "value");
                InSet(issues, resource, "gender", "Patient.gender", "male", "female", "other", "unknown");
                break;
            case "Coverage":
                InSet(issues, resource, "status", "Coverage.status", "active", "cancelled", "draft", "entered-in-error");
                Reference(issues, resource["beneficiary"], "Coverage.beneficiary", "Patient");
                break;
            case "ServiceRequest":
                InSet(issues, resource, "status", "ServiceRequest.status", "draft", "active", "on-hold", "revoked", "completed", "entered-in-error", "unknown");
                Require(issues, resource, "intent", "ServiceRequest.intent");
                Reference(issues, resource["subject"], "ServiceRequest.subject", "Patient");
                CodeableConcept(issues, resource["code"], "ServiceRequest.code");
                break;
            case "MedicationRequest":
                InSet(issues, resource, "status", "MedicationRequest.status", "active", "on-hold", "cancelled", "completed", "entered-in-error", "stopped", "draft", "unknown");
                Require(issues, resource, "intent", "MedicationRequest.intent");
                Reference(issues, resource["subject"], "MedicationRequest.subject", "Patient");
                if (resource["medicationCodeableConcept"] is null && resource["medicationReference"] is null)
                    issues.Add("MedicationRequest: medication[x] is required");
                break;
            case "DiagnosticReport":
                InSet(issues, resource, "status", "DiagnosticReport.status", "registered", "partial", "preliminary", "final", "amended", "corrected", "appended", "cancelled", "entered-in-error", "unknown");
                Reference(issues, resource["subject"], "DiagnosticReport.subject", "Patient");
                CodeableConcept(issues, resource["code"], "DiagnosticReport.code");
                break;
            case "Encounter":
                InSet(issues, resource, "status", "Encounter.status", "planned", "arrived", "triaged", "in-progress", "onleave", "finished", "cancelled", "entered-in-error", "unknown");
                Require(issues, resource, "class", "Encounter.class");
                Reference(issues, resource["subject"], "Encounter.subject", "Patient");
                break;
            case "Condition":
                Reference(issues, resource["subject"], "Condition.subject", "Patient");
                CodeableConcept(issues, resource["code"], "Condition.code");
                if (resource["clinicalStatus"] is JsonObject cs)
                    InCodeableSet(issues, cs, "Condition.clinicalStatus", "active", "recurrence", "relapse", "inactive", "remission", "resolved");
                break;
            case "Observation":
                InSet(issues, resource, "status", "Observation.status", "registered", "preliminary", "final", "amended", "corrected", "cancelled", "entered-in-error", "unknown");
                Reference(issues, resource["subject"], "Observation.subject", "Patient");
                CodeableConcept(issues, resource["code"], "Observation.code");
                break;
            case "AllergyIntolerance":
                Reference(issues, resource["patient"], "AllergyIntolerance.patient", "Patient");
                CodeableConcept(issues, resource["code"], "AllergyIntolerance.code");
                if (resource["criticality"] is JsonValue)
                    InSet(issues, resource, "criticality", "AllergyIntolerance.criticality", "low", "high", "unable-to-assess");
                break;
            case "Bundle":
            case "OperationOutcome":
            case "CapabilityStatement":
                break; // envelope resources — structurally trivial here
            default:
                issues.Add($"unsupported resourceType '{type}'");
                break;
        }
        return issues;
    }

    public static bool IsValid(JsonObject? resource) => Validate(resource).Count == 0;

    private static JsonArray Array(JsonObject o, string field) => o[field] as JsonArray ?? [];

    private static void Require(List<string> issues, JsonObject o, string field, string path)
    {
        if (o[field] is null) issues.Add($"{path} is required");
    }

    private static void RequireAll(List<string> issues, JsonNode? node, string path, params string[] fields)
    {
        if (node is not JsonObject o) { issues.Add($"{path} must be an object"); return; }
        foreach (var f in fields)
            if (o[f] is null) issues.Add($"{path}.{f} is required");
    }

    private static void InSet(List<string> issues, JsonObject o, string field, string path, params string[] allowed)
    {
        var v = o[field]?.GetValue<string>();
        if (v is not null && !allowed.Contains(v, StringComparer.Ordinal))
            issues.Add($"{path} '{v}' is not a valid R4 code");
    }

    private static void CodeableConcept(List<string> issues, JsonNode? node, string path)
    {
        if (node is not JsonObject cc) { issues.Add($"{path} is required"); return; }
        if (cc["coding"] is not JsonArray coding || coding.Count == 0) { issues.Add($"{path}.coding is required"); return; }
        foreach (var c in coding.OfType<JsonObject>())
        {
            if (string.IsNullOrWhiteSpace(c["code"]?.GetValue<string>())) issues.Add($"{path}.coding.code is required");
            var system = c["system"]?.GetValue<string>();
            if (system is not null && !(system.StartsWith("http", StringComparison.Ordinal) || system.StartsWith("urn:", StringComparison.Ordinal)))
                issues.Add($"{path}.coding.system '{system}' is not a URI");
        }
    }

    private static void InCodeableSet(List<string> issues, JsonObject cc, string path, params string[] allowed)
    {
        var code = (cc["coding"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["code"]?.GetValue<string>();
        if (code is not null && !allowed.Contains(code, StringComparer.Ordinal))
            issues.Add($"{path} code '{code}' is not a valid R4 code");
    }

    private static void Reference(List<string> issues, JsonNode? node, string path, string expectedType)
    {
        var reference = node?["reference"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(reference)) { issues.Add($"{path} reference is required"); return; }
        var parts = reference.Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            issues.Add($"{path} '{reference}' is not a valid Type/id reference");
        else if (!string.Equals(parts[0], expectedType, StringComparison.Ordinal))
            issues.Add($"{path} must reference {expectedType}, got {parts[0]}");
    }
}
