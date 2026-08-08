namespace Mersal.Admin.Domain;

/// <summary>The assignable-role catalog and its sensitivity tiering (10-role-matrix §3 + §7). The tier drives
/// access-review cadence: T3/T4 grants are recertified quarterly and auto-expire if unconfirmed.</summary>
public static class RoleCatalog
{
    /// <summary>Highest sensitivity tier each role habitually handles (10-role-matrix §1 tiers).</summary>
    public static readonly IReadOnlyDictionary<string, SensitivityTier> Tiers =
        new Dictionary<string, SensitivityTier>(StringComparer.Ordinal)
        {
            ["reception"] = SensitivityTier.T1,
            ["call_center"] = SensitivityTier.T2,
            ["beneficiary_mgmt"] = SensitivityTier.T2,
            ["finance"] = SensitivityTier.T2,
            ["network_team"] = SensitivityTier.T2,
            ["claims_officer"] = SensitivityTier.T2,
            ["case_manager"] = SensitivityTier.T3,
            ["doctor"] = SensitivityTier.T3,
            ["nurse"] = SensitivityTier.T3,
            ["lab_tech"] = SensitivityTier.T3,
            // 29.1 (design 45 §1) — both spellings for the dual-accept window; `imaging_tech` goes at the
            // contract step. Listing both is not belt-and-braces here, it is required: TierOf falls back to
            // T1 for an unknown role, and T1 is the LEAST sensitive tier — so a radiology_tech missing from
            // this table would not fail, it would quietly drop out of quarterly access recertification. A
            // rename that de-tiers a T3 clinical role is a compliance regression that nothing else catches.
            ["imaging_tech"] = SensitivityTier.T3,
            ["radiology_tech"] = SensitivityTier.T3,
            // 29.2b — the external delivering provider. T3: it sees a beneficiary's identity and the service
            // ordered for them, so it recertifies quarterly like any other clinical grant.
            ["procedure_provider"] = SensitivityTier.T3,
            ["pharmacist"] = SensitivityTier.T3,
            ["medical_approval"] = SensitivityTier.T3,
            ["medical_director"] = SensitivityTier.T3,
            // 19.7 — benefit administration. T2: entitlement and money, never a diagnosis.
            ["policy_admin"] = SensitivityTier.T2,
            ["beneficiary_mgmt_supervisor"] = SensitivityTier.T2,
            // 25.1 — branch management (design 42 §1). ONE permission set, two reaches: the coordinator runs
            // one clinic, the manager supervises all six, and both hold the same sixteen scopes. T2 because
            // they administer staff licence data and clinic stock, never a diagnosis — their clinical reach is
            // reception's and no wider.
            ["branch_coordinator"] = SensitivityTier.T2,
            ["clinics_manager"] = SensitivityTier.T2,
            ["provider_admin"] = SensitivityTier.T4,
            ["org_admin"] = SensitivityTier.T4,
            ["super_admin"] = SensitivityTier.T4,
        };

    public static SensitivityTier TierOf(string role) =>
        Tiers.TryGetValue(role.Trim().ToLowerInvariant(), out var t) ? t : SensitivityTier.T1;

    /// <summary>True if the role is known/assignable.</summary>
    public static bool IsKnown(string role) => Tiers.ContainsKey(role.Trim().ToLowerInvariant());

    /// <summary>T3/T4 grants require periodic recertification (quarterly access review).</summary>
    public static bool RequiresReview(SensitivityTier tier) => tier is SensitivityTier.T3 or SensitivityTier.T4;
}
