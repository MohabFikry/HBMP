namespace Mersal.Admin.Domain;

/// <summary>The governed master-data code systems (FR-MDM-007/009). Edits are effective-dated per system+code.</summary>
public enum CodeSystem { Icd10, Cpt, Loinc, Atc, Drug, DrugInteraction, Allergen, Formulary }

/// <summary>
/// Which code systems clinical governance may edit, and which stay with the platform administrators.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0035 §4 opened this as a question and answered it this way: the Medical Director holds master-data
/// editing on the principle that <b>they absorb the consequence of getting it wrong</b> — a mis-mapped ICD
/// code misroutes a diagnosis into their own approval queue, a wrong ATC entry breaks the interaction check
/// their reviewers rely on. That argument reaches exactly as far as the clinical vocabularies and no further.
/// A director does not live with the consequence of a wrong formulary tier or a wrong allergen grouping in
/// the same way, and "they can already edit some of it" is not a reason to hand over the rest.
/// </para>
/// <para>
/// <b>This narrows nobody's existing access.</b> <c>super_admin</c> keeps every system. The list exists so a
/// role that was granted the action for clinical reasons cannot quietly acquire administrative reach with it.
/// </para>
/// </remarks>
public static class MasterDataGovernance
{
    /// <summary>The clinical vocabularies: a diagnosis, a procedure, a lab analyte, a drug class.</summary>
    public static readonly IReadOnlySet<CodeSystem> ClinicalSystems =
        new HashSet<CodeSystem> { CodeSystem.Icd10, CodeSystem.Cpt, CodeSystem.Loinc, CodeSystem.Atc };

    /// <summary>Roles whose master-data authority is bounded to <see cref="ClinicalSystems"/>.</summary>
    private static readonly IReadOnlySet<string> ClinicalOnlyRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "medical_director" };

    /// <summary>Roles that reach every system.</summary>
    private static readonly IReadOnlySet<string> UnboundedRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "super_admin" };

    /// <summary>
    /// May this caller edit this system?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the caller's whole role SET, not one role: a principal may hold several, and asking about one of
    /// them would answer a different question depending on which. An unbounded role anywhere in the set wins
    /// — holding <c>super_admin</c> as well as <c>medical_director</c> is more authority, never less.
    /// </para>
    /// <para>
    /// Unknown roles are NOT rejected here. The ABAC gate has already decided whether the caller may edit
    /// master data at all; this is the narrower second question, and repeating the first one would put the
    /// same decision in two places, which is how the two come to disagree.
    /// </para>
    /// </remarks>
    public static bool MayEdit(IReadOnlySet<string>? roles, CodeSystem system)
    {
        if (roles is null || roles.Count == 0) return true;
        if (roles.Any(UnboundedRoles.Contains)) return true;
        return !roles.Any(ClinicalOnlyRoles.Contains) || ClinicalSystems.Contains(system);
    }
}

/// <summary>
/// An effective-dated version of a single master-data code (FR-MDM-007). A governance edit APPENDS a new version
/// (closing the prior version's <see cref="EffectiveTo"/>) — it never mutates history, so an order/prescription
/// resolves to the version in force at ITS time. Attributes are held as a JSON snapshot so every code system reuses
/// one table. Restricted to clinical-governance roles and audited.
/// </summary>
public sealed class MasterDataVersion
{
    public Guid VersionId { get; set; }
    public CodeSystem System { get; set; }
    public string Code { get; set; } = default!;
    public int VersionNo { get; set; }
    public string AttributesJson { get; set; } = "{}";
    public bool Retired { get; set; }                 // a version can retire the code (no successor)
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }  // null = currently in force
    public string ChangedBy { get; set; } = default!;
    public string Rationale { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>True if this version is the one in force at <paramref name="asOf"/>.</summary>
    public bool InForceAt(DateTimeOffset asOf) =>
        asOf >= EffectiveFrom && (EffectiveTo is null || asOf < EffectiveTo);
}

/// <summary>
/// A bilingual notification template version (FR-NOT-005). Both AR and EN bodies are authored (parity required); a
/// template bound to an outbound channel (SMS/email) must carry NO clinical/PHI token in its body (data
/// minimization) — enforced by <see cref="TemplateLinter"/> before save. Effective-dated + audited.
/// </summary>
public sealed class NotificationTemplateVersion
{
    public Guid TemplateVersionId { get; set; }
    public string TenantId { get; set; } = default!;
    public string TemplateKey { get; set; } = default!;   // e.g. approval-decision
    public string Channel { get; set; } = default!;        // in_app | email | sms | whatsapp
    public string SubjectEn { get; set; } = "";
    public string SubjectAr { get; set; } = "";
    public string BodyEn { get; set; } = default!;
    public string BodyAr { get; set; } = default!;
    public int VersionNo { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public string ChangedBy { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The typed shape a config value must parse as (typed, validated settings — FR-MDM config).</summary>
public enum ConfigValueType { Text, Whole, Number, Boolean, Duration }

/// <summary>
/// An effective-dated, typed system-configuration value (feature flags, thresholds like the high-cost approval
/// trigger, reminder lead-times). Tenant-scoped (<c>tenant_id</c>) or platform-level (tenant_id = "*"). A change
/// appends a new version so a historical decision can resolve the value in force at its time; validated + audited.
/// </summary>
public sealed class SystemConfig
{
    public Guid ConfigId { get; set; }
    public string TenantId { get; set; } = default!;   // "*" = platform-level
    public string Key { get; set; } = default!;
    public ConfigValueType ValueType { get; set; }
    public string Value { get; set; } = default!;      // canonical string form (validated for the type)
    public int VersionNo { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public string UpdatedBy { get; set; } = default!;
    public DateTimeOffset UpdatedAt { get; set; }
}
