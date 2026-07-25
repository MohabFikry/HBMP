namespace Mersal.Admin.Domain;

/// <summary>The governed master-data code systems (FR-MDM-007/009). Edits are effective-dated per system+code.</summary>
public enum CodeSystem { Icd10, Cpt, Loinc, Atc, Drug, DrugInteraction, Allergen, Formulary }

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
