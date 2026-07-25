namespace Mersal.Authz;

/// <summary>
/// The clinical-EMR policy overlay (phase 4). It extends <see cref="DefaultPolicies"/> with write rules for
/// clinical documentation and read rules for the clinical record, all gated by the <b>treating-relationship</b>
/// ABAC condition (US-030) for doctors/nurses. The medical-approval team may read the clinical record WITHOUT a
/// treating relationship (11-permission-matrix.md); non-clinical roles (reception/labs/pharmacy/finance) have no
/// rule here at all, so they are default-denied. emr-service authorizes with <see cref="Bundle"/>.
/// See 18-security-model.md §4 and 11-permission-matrix.md.
/// </summary>
public static class EmrPolicies
{
    public const string Version = "4.0";

    /// <summary>Read action for the treating clinician (requires a treating relationship).</summary>
    public const string Read = "emr:read";
    /// <summary>Read action for the medical-approval team — clinical OVERSIGHT read, no treating relationship
    /// required (a distinct action because the engine gates one rule per action+resource; the gate selects it
    /// by role). Kept distinct so the two read purposes are separately auditable.</summary>
    public const string ReadOversight = "emr:read-oversight";
    public const string Write = "emr:write";

    // Clinical resource types the EMR authorizes. "encounter" already has an emr:read rule in DefaultPolicies.
    public static class Resources
    {
        public const string Encounter = "encounter";
        public const string Note = "emr_note";
        public const string Diagnosis = "diagnosis";
        public const string Vital = "vital";
        public const string Allergy = "allergy";
        public const string MedicationHistory = "medication_history";
    }

    /// <summary>The EMR rules on their own, so other services (orders/pharmacy) can splice in the treating check.</summary>
    public static IReadOnlyList<PolicyRule> Rules()
    {
        var clinical = new[]
        {
            Resources.Encounter, Resources.Note, Resources.Diagnosis,
            Resources.Vital, Resources.Allergy, Resources.MedicationHistory,
        };

        var rules = new List<PolicyRule>();
        foreach (var res in clinical)
        {
            // Doctor/nurse WRITE clinical data — requires tenant match + a treating relationship.
            rules.Add(new PolicyRule
            {
                Action = Write, ResourceType = res,
                Roles = Set("doctor", "nurse"), Scopes = Set("emr:write"),
                RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
                Sensitive = true,
            });
            // Doctor/nurse READ clinical data — requires tenant match + a treating relationship.
            rules.Add(new PolicyRule
            {
                Action = Read, ResourceType = res,
                Roles = Set("doctor", "nurse"), Scopes = Set("emr:read"),
                RequiredConditions = [AbacConditions.TenantMatch, AbacConditions.TreatingRelationship],
                Sensitive = true,
            });
            // Medical-approval team OVERSIGHT READ — tenant only, NO treating relationship required.
            rules.Add(new PolicyRule
            {
                Action = ReadOversight, ResourceType = res,
                Roles = Set("medical_approval", "medical_director"), Scopes = Set("emr:read"),
                RequiredConditions = [AbacConditions.TenantMatch],
                Sensitive = true,
            });
        }
        return rules;
    }

    /// <summary>Full bundle = platform defaults + EMR clinical rules. emr-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        // Drop the base emr:read/encounter rule so our per-resource rules are the single source (bundle.Match
        // takes the first matching rule; keeping both would be ambiguous). Everything else is preserved.
        var kept = baseBundle.Rules.Where(r => !(r.Action == "emr:read" && r.ResourceType == Resources.Encounter));
        return new PolicyBundle(Version, [.. kept, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
