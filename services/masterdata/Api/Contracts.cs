namespace Mersal.MasterData.Api;

/// <summary>Body for POST /drug-interactions/check.</summary>
public sealed record DrugCheckRequest(string[] DrugCodes);

/// <summary>Body for POST /allergies/check.</summary>
public sealed record AllergyCheckRequest(string DrugCode, string[] PatientAllergenCodes);
