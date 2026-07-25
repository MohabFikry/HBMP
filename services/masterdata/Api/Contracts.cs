namespace Mersal.MasterData.Api;

/// <summary>Body for POST /drug-interactions/check.</summary>
public sealed record DrugCheckRequest(string[] DrugCodes);

/// <summary>Body for POST /allergies/check.</summary>
public sealed record AllergyCheckRequest(string DrugCode, string[] PatientAllergenCodes);

/// <summary>Body for POST /drug-interactions/check-by-ids (pharmacy uses drug uuids).</summary>
public sealed record DrugIdCheckRequest(Guid[] DrugIds);

/// <summary>Body for POST /allergies/check-by-ids (pharmacy uses drug + allergen uuids).</summary>
public sealed record AllergyIdCheckRequest(Guid DrugId, Guid[] AllergenIds);
