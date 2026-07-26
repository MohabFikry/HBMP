using System.Globalization;
using System.Text.Json.Nodes;

namespace Mersal.Interop.Domain.Mapping;

/// <summary>The outcome of translating an inbound FHIR resource into a native command: EITHER a native command
/// payload to POST to the owning service, OR a FHIR OperationOutcome describing why the resource was rejected.</summary>
public sealed record TranslationResult(JsonObject? Command, JsonObject? Error)
{
    public bool Ok => Error is null && Command is not null;
    public static TranslationResult Fail(string code, string diagnostics) =>
        new(null, Fhir.Fhir.OperationOutcome("error", code, diagnostics));
    public static TranslationResult Success(JsonObject command) => new(command, null);
}

/// <summary>
/// Translates safe inbound FHIR R4 writes into the OWNING service's native command shape (13.1). Writes are only
/// accepted for resources where a create is sensible and safe (ServiceRequest/referral, MedicationRequest,
/// Observation, AllergyIntolerance); derived/immutable resources (DiagnosticReport, Condition, Encounter,
/// Patient, Coverage) are rejected upstream with an OperationOutcome. The native command is then POSTed by the
/// façade to the sibling, which applies its own authorization, validation, and audit — the façade adds no
/// shortcut. Pure + unit-testable; no I/O.
/// </summary>
public static class WriteTranslators
{
    public static TranslationResult ServiceRequest(JsonObject? fhir)
    {
        if (fhir is null) return TranslationResult.Fail("structure", "Missing request body.");
        if (!IsType(fhir, "ServiceRequest")) return TranslationResult.Fail("invalid", "resourceType must be ServiceRequest.");
        var subject = PatientRef(fhir, "subject");
        if (subject is null) return TranslationResult.Fail("required", "ServiceRequest.subject (Patient reference) is required.");
        var (system, code, display) = FirstCoding(fhir["code"]);
        if (code is null) return TranslationResult.Fail("required", "ServiceRequest.code is required.");
        var intent = fhir["category"] is JsonArray cats && HasReferralCategory(cats) ? "referral" : "order";
        return TranslationResult.Success(new JsonObject
        {
            ["beneficiaryId"] = subject,
            ["intent"] = intent,
            ["code"] = code,
            ["codeSystem"] = system,
            ["display"] = display,
            ["quantity"] = QuantityValue(fhir["quantityQuantity"]),
        });
    }

    public static TranslationResult MedicationRequest(JsonObject? fhir)
    {
        if (fhir is null) return TranslationResult.Fail("structure", "Missing request body.");
        if (!IsType(fhir, "MedicationRequest")) return TranslationResult.Fail("invalid", "resourceType must be MedicationRequest.");
        var subject = PatientRef(fhir, "subject");
        if (subject is null) return TranslationResult.Fail("required", "MedicationRequest.subject (Patient reference) is required.");
        var (system, code, display) = FirstCoding(fhir["medicationCodeableConcept"]);
        if (code is null) return TranslationResult.Fail("required", "MedicationRequest.medicationCodeableConcept is required.");
        var dosage = (fhir["dosageInstruction"] as JsonArray)?.FirstOrDefault()?["text"]?.GetValue<string>();
        return TranslationResult.Success(new JsonObject
        {
            ["beneficiaryId"] = subject,
            ["medicationCode"] = code,
            ["codeSystem"] = system,
            ["display"] = display,
            ["dosageText"] = dosage,
            ["quantity"] = QuantityValue(fhir["dispenseRequest"]?["quantity"]),
        });
    }

    public static TranslationResult Observation(JsonObject? fhir)
    {
        if (fhir is null) return TranslationResult.Fail("structure", "Missing request body.");
        if (!IsType(fhir, "Observation")) return TranslationResult.Fail("invalid", "resourceType must be Observation.");
        var subject = PatientRef(fhir, "subject");
        if (subject is null) return TranslationResult.Fail("required", "Observation.subject (Patient reference) is required.");
        var (system, code, display) = FirstCoding(fhir["code"]);
        if (code is null) return TranslationResult.Fail("required", "Observation.code is required.");
        return TranslationResult.Success(new JsonObject
        {
            ["beneficiaryId"] = subject,
            ["code"] = code,
            ["codeSystem"] = system,
            ["display"] = display,
            ["value"] = QuantityValue(fhir["valueQuantity"]),
            ["unit"] = fhir["valueQuantity"]?["unit"]?.GetValue<string>(),
            ["category"] = FirstCategoryCode(fhir["category"]) ?? "vital-signs",
        });
    }

    public static TranslationResult AllergyIntolerance(JsonObject? fhir)
    {
        if (fhir is null) return TranslationResult.Fail("structure", "Missing request body.");
        if (!IsType(fhir, "AllergyIntolerance")) return TranslationResult.Fail("invalid", "resourceType must be AllergyIntolerance.");
        var subject = PatientRef(fhir, "patient");
        if (subject is null) return TranslationResult.Fail("required", "AllergyIntolerance.patient (Patient reference) is required.");
        var (system, code, display) = FirstCoding(fhir["code"]);
        if (code is null) return TranslationResult.Fail("required", "AllergyIntolerance.code is required.");
        var reaction = (fhir["reaction"] as JsonArray)?.FirstOrDefault()?["manifestation"] is JsonArray man
            ? man.FirstOrDefault()?["text"]?.GetValue<string>()
            : null;
        return TranslationResult.Success(new JsonObject
        {
            ["beneficiaryId"] = subject,
            ["code"] = code,
            ["codeSystem"] = system,
            ["display"] = display,
            ["criticality"] = fhir["criticality"]?.GetValue<string>(),
            ["reaction"] = reaction,
        });
    }

    private static bool IsType(JsonObject o, string type) =>
        string.Equals(o["resourceType"]?.GetValue<string>(), type, StringComparison.Ordinal);

    /// <summary>Resolve a "Patient/{id}" reference at <paramref name="field"/> → the bare id.</summary>
    private static string? PatientRef(JsonObject o, string field)
    {
        var reference = o[field]?["reference"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(reference)) return null;
        const string prefix = "Patient/";
        return reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..] : null;
    }

    private static (string? System, string? Code, string? Display) FirstCoding(JsonNode? codeableConcept)
    {
        if (codeableConcept?["coding"] is JsonArray coding && coding.FirstOrDefault() is JsonObject c)
            return (c["system"]?.GetValue<string>(), c["code"]?.GetValue<string>(), c["display"]?.GetValue<string>());
        return (null, null, null);
    }

    private static decimal? QuantityValue(JsonNode? quantity)
    {
        var v = quantity?["value"];
        if (v is null) return null;
        return v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? v.GetValue<decimal>()
            : decimal.TryParse(v.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static bool HasReferralCategory(JsonArray categories) =>
        categories.OfType<JsonObject>().Any(cc =>
            (cc["coding"] as JsonArray)?.OfType<JsonObject>()
                .Any(c => string.Equals(c["display"]?.GetValue<string>(), "Referral to service", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(c["code"]?.GetValue<string>(), "306206005", StringComparison.Ordinal)) == true);

    private static string? FirstCategoryCode(JsonNode? category) =>
        (category as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["coding"] is JsonArray coding
            ? coding.OfType<JsonObject>().FirstOrDefault()?["code"]?.GetValue<string>()
            : null;
}
