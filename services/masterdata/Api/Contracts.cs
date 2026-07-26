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

/// <summary>Body for POST /allergies/check.</summary>
public sealed record AllergyCheckRequest(string DrugCode, string[] PatientAllergenCodes);

/// <summary>Body for POST /drug-interactions/check-by-ids (pharmacy uses drug uuids).</summary>
public sealed record DrugIdCheckRequest(Guid[] DrugIds);

/// <summary>Body for POST /allergies/check-by-ids (pharmacy uses drug + allergen uuids).</summary>
public sealed record AllergyIdCheckRequest(Guid DrugId, Guid[] AllergenIds);
