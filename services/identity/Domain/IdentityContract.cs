namespace Mersal.Identity.Domain;

/// <summary>
/// The frozen identity vocabulary the store MUST seed, mirroring docs/security/token-contract.md §2 (the 17
/// role names) so the roles-as-data seed and its tests share one source of truth. The role→scope MATRIX
/// itself lives in the migration seed (data), not here — this only pins the role NAMES that must exist.
/// </summary>
public static class IdentityContract
{
    /// <summary>The frozen, lower-case role vocabulary (authoritative catalog: admin RoleCatalog + the realm).</summary>
    public static readonly IReadOnlyList<string> Roles =
    [
        "reception", "call_center", "beneficiary_mgmt", "finance", "network_team", "claims_officer",
        "case_manager", "doctor", "nurse", "lab_tech", "imaging_tech", "pharmacist",
        "medical_approval", "medical_director", "provider_admin", "org_admin", "super_admin",
    ];
}
