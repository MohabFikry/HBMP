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
            ["case_manager"] = SensitivityTier.T3,
            ["doctor"] = SensitivityTier.T3,
            ["nurse"] = SensitivityTier.T3,
            ["lab_tech"] = SensitivityTier.T3,
            ["imaging_tech"] = SensitivityTier.T3,
            ["pharmacist"] = SensitivityTier.T3,
            ["medical_approval"] = SensitivityTier.T3,
            ["medical_director"] = SensitivityTier.T3,
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
