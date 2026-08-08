namespace Mersal.MasterData.Domain;

/// <summary>OAuth2 scopes for the reference catalogue (11-permission-matrix, phase 26.1).</summary>
public static class MasterDataScopes
{
    /// <summary>
    /// Read the reference catalogue — ICD-10, CPT, LOINC, ATC, drugs, indications, interactions, allergens.
    /// </summary>
    /// <remarks>
    /// Held by nearly every clinical role, which is intended: a diagnosis code means the same thing to a
    /// doctor, a pharmacist and a claims officer, and withholding it would break their screens without
    /// protecting anything. Its purpose is that reference-data reach is stated rather than assumed, so it
    /// appears in the role matrix, can be withdrawn from a role, and is not handed to a machine token by
    /// default. There is deliberately no write scope: master data changes through admin-service's governed,
    /// effective-dated, audited path (8b.2).
    /// </remarks>
    public const string Read = "masterdata:read";
}
