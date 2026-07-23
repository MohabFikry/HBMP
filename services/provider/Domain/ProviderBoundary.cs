namespace Mersal.Provider.Domain;

/// <summary>The ONLY beneficiary shape allowed to cross the provider boundary for fulfillment (2b.3, layer 5
/// of THE INVARIANT; 18-security-model §8 minimum-necessary). A Lab/Imaging/Pharmacy provider receives just
/// enough to identify the sample/order and nothing clinical — no diagnoses, notes, prescriptions, results,
/// or contact PII. New fields must stay within this whitelist; a reflection test (MinNecessaryTests) fails
/// the build if a forbidden term ever appears here. Phases 5/6 project their order payloads through this.</summary>
public sealed record ProviderBoundaryPatient(
    Guid BeneficiaryRef,   // opaque reference, not the national/UNHCR id
    string MemberNo,       // MRS-M-* benefit card number
    string Initials,       // e.g. "A.M." — enough to match at the counter, not the full name
    string Sex,
    int AgeYears,
    string OrderedServiceType,   // Lab | Imaging | Consult | Procedure
    string OrderedCode)          // the CPT/LOINC/LOCAL code being fulfilled — the permitted indication
{
    /// <summary>Field-name fragments that must never appear on a provider-boundary payload. Enforced by test.</summary>
    public static readonly string[] ForbiddenTerms =
    [
        "diagnos", "icd", "note", "prescription", "medication",
        "result", "finding", "address", "phone", "nationalid", "passport", "refugee", "unhcr",
    ];
}
