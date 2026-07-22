namespace Mersal.Authz;

/// <summary>
/// The starter policy bundle + field-access matrix, encoding the hard rules of
/// 11-permission-matrix.md. Grows per phase (each service adds its rules); this seeds phase 0 and the
/// authorization tests. Versioned so a change is a reviewable, audited bundle deploy.
/// </summary>
public static class DefaultPolicies
{
    public const string Version = "0.4.0";

    // Field classes used across the platform.
    public static class Classes
    {
        public const string Diagnosis = "diagnosis";
        public const string Clinical = "clinical";        // notes
        public const string Prescription = "prescription";
        public const string Result = "result";            // lab/imaging results
        public const string Financials = "financials";
        public const string Coverage = "coverage";
        public const string Identity = "identity";
    }

    public static PolicyBundle Bundle() => new(Version,
    [
        // A treating doctor may read a patient's EMR; requires treating-relationship + tenant.
        new PolicyRule
        {
            Action = "emr:read", ResourceType = "encounter",
            Roles = Set("doctor", "nurse"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
            Sensitive = true,
        },
        // A lab/imaging provider may read an order line only for its own provider.
        new PolicyRule
        {
            Action = "orders:read", ResourceType = "order_line",
            Roles = Set("lab_tech", "imaging_tech"),
            Scopes = Set("orders:read"),
            RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.ProviderOwnership],
            Sensitive = true,
        },
        // Reception may read the eligibility card (no clinical) — tenant only.
        new PolicyRule
        {
            Action = "reception:read", ResourceType = "beneficiary_card",
            Roles = Set("reception", "beneficiary_mgmt"),
            Scopes = Set("reception:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Medical approval may review clinical context under purpose.
        new PolicyRule
        {
            Action = "auth:review", ResourceType = "authorization",
            Roles = Set("medical_approval", "medical_director"),
            Scopes = Set("auth:review"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ]);

    /// <summary>Role → readable field-classes (11-permission-matrix.md §4). Baseline classes are implicit.</summary>
    public static FieldAccessMatrix FieldMatrix() => new(new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["doctor"] = Set(Classes.Diagnosis, Classes.Clinical, Classes.Prescription, Classes.Result, Classes.Coverage),
        ["nurse"] = Set(Classes.Clinical, Classes.Result, Classes.Coverage),
        ["reception"] = Set(Classes.Coverage),                    // NOT diagnosis/clinical
        ["beneficiary_mgmt"] = Set(Classes.Coverage),
        ["lab_tech"] = Set(Classes.Result),                       // NOT prescription
        ["imaging_tech"] = Set(Classes.Result),                   // NOT prescription
        ["pharmacist"] = Set(Classes.Prescription),               // NOT result
        ["finance"] = Set(Classes.Financials),                    // NOT diagnosis
        ["medical_approval"] = Set(Classes.Diagnosis, Classes.Clinical, Classes.Result, Classes.Prescription),
        ["medical_director"] = Set(Classes.Diagnosis, Classes.Clinical, Classes.Result, Classes.Prescription, Classes.Financials),
        ["case_manager"] = Set(Classes.Coverage, Classes.Diagnosis),  // diagnosis at coordination summary only
    });

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
