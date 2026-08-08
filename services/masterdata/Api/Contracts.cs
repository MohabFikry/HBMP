using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Api;

/// <summary>Body for POST /drug-interactions/check.</summary>
public sealed record DrugCheckRequest(string[] DrugCodes);

/// <summary>14.6 examination-type projection. Carries the pinned sensitivity orders resolves at creation.</summary>
public sealed record ExamView(
    Guid ExaminationTypeId, string Code, string NameEn, string NameAr, string Category,
    string DefaultCodeSystem, string? DefaultCode, string SensitivityLevel, string? SensitiveCategory)
{
    public static ExamView Of(ExaminationType x) => new(
        x.ExaminationTypeId, x.Code, x.NameEn, x.NameAr, x.Category.ToString(),
        x.DefaultCodeSystem, x.DefaultCode, x.SensitivityLevel.ToString(), x.SensitiveCategory?.ToString());
}

/// <summary>Body for POST /examination-types/prices/by-codes. A code is the catalogue's own short code (CXR)
/// or its default billing code (71046) — an order line records the latter.</summary>
public sealed record ExamPriceRequest(string[] Codes);

/// <summary>Body for POST /allergies/check.</summary>
public sealed record AllergyCheckRequest(string DrugCode, string[] PatientAllergenCodes);

/// <summary>
/// Body for POST /dosing-rules/by-ids (28.10).
/// </summary>
/// <param name="Population">
/// The patient's age band. NULL when no age is recorded, and no population-specific rule is then selected —
/// an adult ceiling silently applied to a four-year-old is the absence of a check wearing its clothes.
/// </param>
/// <param name="WeightKg">
/// Used only to resolve a mg/kg rule into a daily maximum. NULL leaves a weight-based rule's ceiling null,
/// and the engine reports the missing weight by name.
/// </param>
public sealed record DosingRuleRequest(
    Guid[] DrugIds, string[] DiagnosisIcdCodes, string? Population, string? Route, decimal? WeightKg);

/// <summary>
/// Body for POST /contraindications/check-by-ids (28.9).
/// </summary>
/// <param name="IsPregnant">
/// True only when the status is RECORDED as pregnant. Unknown must not count as pregnant: a rule firing on
/// every patient nobody has asked about would be dismissed within a day, and would stop meaning anything for
/// the patients it was written for.
/// </param>
public sealed record ContraindicationCheckRequest(
    Guid[] DrugIds, string[] DiagnosisIcdCodes, bool IsPregnant);

/// <summary>Body for POST /icd-codes/ancestors — the hierarchy walk behind the indication check (28.7).</summary>
public sealed record IcdCodeListRequest(string[] Codes);

/// <summary>Body for POST /drug-interactions/check-by-ids (pharmacy uses drug uuids).</summary>
public sealed record DrugIdCheckRequest(Guid[] DrugIds);

/// <summary>Body for POST /allergies/check-by-ids (pharmacy uses drug + allergen uuids).</summary>
public sealed record AllergyIdCheckRequest(Guid DrugId, Guid[] AllergenIds);
