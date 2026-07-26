namespace Mersal.Authz;

/// <summary>
/// The FHIR R4 façade authorization overlay (phase 13.1). The façade at <c>/fhir/r4</c> is an ADAPTER over the
/// core services — it owns no clinical data and is NEVER an authorization bypass. Two layers enforce
/// minimum-necessary, exactly as the native APIs do (11-permission-matrix, 18-security-model §4):
///
///  1. THIS coarse gate — role + scope + tenant, evaluated at the policy layer so every deny is audited by the
///     engine. Each FHIR interaction (resource × verb) is a distinct action; the role set per resource is the
///     min-necessary anchor. A role that cannot read a diagnosis natively is simply absent from the
///     <c>Condition</c> read rule, so Finance/Reception/Pharmacy/Lab GET /fhir/r4/Condition → default-deny.
///  2. Field- and record-level ABAC (treating-relationship, provider-ownership, sensitive-result release) is
///     enforced by the OWNING service when the façade reads/writes through it under the caller's bearer token
///     (defense in depth — the façade re-implements none of it). See <c>docs/adr/0016-fhir-facade-interop.md</c>.
///
/// Scopes are SMART-on-FHIR style (<c>fhir:read:Patient</c>, <c>fhir:write:ServiceRequest</c>): additive scopes
/// granted to integration clients, on top of — not replacing — the frozen core token contract (Phase 17). The
/// rule's scope set is any-of, so a token bearing the SMART scope is accepted; the role set is the hard
/// min-necessary boundary regardless of which scopes a token happens to carry.
/// </summary>
public static class InteropPolicies
{
    public const string Version = "13.0";

    /// <summary>The single resource type all FHIR-façade rules match on; the specific interaction is the action.</summary>
    public const string Resource = "fhir";

    /// <summary>Resource type for integration-governance actions (partner registry, enablement, inbound ingest).</summary>
    public const string GovernanceResource = "interop-admin";

    // Integration-governance actions (13.2) — administering the partner registry + DPIA-gated enablement.
    public const string PartnerRead = "interop:partner:read";
    public const string PartnerManage = "interop:partner:manage";
    public const string InboundIngest = "interop:inbound:ingest";

    // ---- SMART-on-FHIR interaction scopes (additive; advertised in the CapabilityStatement) -----------------
    public static string ReadScope(string resource) => $"fhir:read:{resource}";
    public static string WriteScope(string resource) => $"fhir:write:{resource}";

    // ---- Actions: one per resource × verb (the CapabilityStatement lists exactly these) ---------------------
    public static string ReadAction(string resource) => $"fhir:{resource}:read";
    public static string WriteAction(string resource) => $"fhir:{resource}:write";

    /// <summary>Supported resource names (FHIR R4), per 17-api-specifications §12.</summary>
    public const string Patient = "Patient";
    public const string Coverage = "Coverage";
    public const string ServiceRequest = "ServiceRequest";
    public const string MedicationRequest = "MedicationRequest";
    public const string DiagnosticReport = "DiagnosticReport";
    public const string Encounter = "Encounter";
    public const string Condition = "Condition";
    public const string Observation = "Observation";
    public const string AllergyIntolerance = "AllergyIntolerance";

    /// <summary>The FHIR-façade rules. Read role sets are grounded in 11-permission-matrix §2/§3 and mirror the
    /// native scope grants in <c>identity 0001_identity.sql</c>: e.g. Finance/Reception/Pharmacy/Lab hold no
    /// emr/diagnosis capability, so they are absent from Condition/DiagnosticReport reads.</summary>
    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // ----- READS -----
        // Patient (beneficiary demographics/identifiers) — the roles that look a beneficiary up natively. Finance
        // is intentionally EXCLUDED (it reaches financial context via Coverage, not full demographics).
        Read(Patient, "reception", "doctor", "nurse", "case_manager", "call_center",
                      "pharmacist", "lab_tech", "imaging_tech", "medical_approval", "medical_director"),
        // Coverage (policy/coverage/limits — financial context). Finance + eligibility/coordination roles.
        Read(Coverage, "reception", "finance", "case_manager", "call_center", "claims_officer", "medical_director"),
        // ServiceRequest (investigation orders / referrals) — ordering + fulfilling roles.
        Read(ServiceRequest, "doctor", "nurse", "lab_tech", "imaging_tech", "case_manager"),
        // MedicationRequest (prescriptions) — prescriber + dispenser.
        Read(MedicationRequest, "doctor", "nurse", "pharmacist"),
        // DiagnosticReport (results) — clinician + the fulfilling lab/imaging.
        Read(DiagnosticReport, "doctor", "lab_tech", "imaging_tech"),
        // Observation (vitals + result values) — clinical + fulfilling roles.
        Read(Observation, "doctor", "nurse", "lab_tech", "imaging_tech"),
        // Encounter — clinical + coordination.
        Read(Encounter, "doctor", "nurse", "case_manager"),
        // Condition (DIAGNOSIS) — clinical + approval oversight ONLY. Finance/Reception/Pharmacy/Lab are absent
        // by design → GET /fhir/r4/Condition is default-denied for them (the min-necessary parity guarantee).
        Read(Condition, "doctor", "nurse", "medical_approval", "medical_director", "case_manager"),
        // AllergyIntolerance — clinical + pharmacist (dispensing safety).
        Read(AllergyIntolerance, "doctor", "nurse", "pharmacist"),

        // ----- WRITES (translate to the owning service's native command) -----
        // ServiceRequest / referral create — prescriber.
        Write(ServiceRequest, "doctor"),
        // MedicationRequest create — prescriber.
        Write(MedicationRequest, "doctor"),
        // Observation (vital) create — clinician / nurse.
        Write(Observation, "doctor", "nurse"),
        // AllergyIntolerance create — clinician / nurse.
        Write(AllergyIntolerance, "doctor", "nurse"),
        // NB: Patient, Coverage, DiagnosticReport, Encounter, Condition are read-only/derived via the façade —
        // there is deliberately NO write rule, and the endpoints reject POST with an OperationOutcome.

        // ----- INTEGRATION GOVERNANCE (13.2) — administer the partner registry + DPIA-gated enablement -----
        new PolicyRule
        {
            Action = PartnerRead, ResourceType = GovernanceResource,
            Roles = new HashSet<string>(["super_admin", "org_admin"], StringComparer.Ordinal),
            Scopes = new HashSet<string>(["admin:read"], StringComparer.Ordinal),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        new PolicyRule
        {
            Action = PartnerManage, ResourceType = GovernanceResource,
            Roles = new HashSet<string>(["super_admin", "org_admin"], StringComparer.Ordinal),
            Scopes = new HashSet<string>(["admin:write"], StringComparer.Ordinal),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Inbound ingest is a machine-to-machine receipt (the partner posts). Restricted to the integration
        // admin/system scope; every message is staged + audited regardless (anti-corruption boundary).
        new PolicyRule
        {
            Action = InboundIngest, ResourceType = GovernanceResource,
            Roles = new HashSet<string>(["super_admin", "org_admin"], StringComparer.Ordinal),
            Scopes = new HashSet<string>(["admin:write"], StringComparer.Ordinal),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ];

    private static PolicyRule Read(string resource, params string[] roles) => new()
    {
        Action = ReadAction(resource),
        ResourceType = Resource,
        Roles = new HashSet<string>(roles, StringComparer.Ordinal),
        Scopes = new HashSet<string>([ReadScope(resource)], StringComparer.Ordinal),
        RequiredConditions = [AbacConditions.TenantMatch],
        // Diagnosis/result/med reads are PHI reads → audit even on allow.
        Sensitive = resource is Condition or DiagnosticReport or Observation or MedicationRequest or AllergyIntolerance,
    };

    private static PolicyRule Write(string resource, params string[] roles) => new()
    {
        Action = WriteAction(resource),
        ResourceType = Resource,
        Roles = new HashSet<string>(roles, StringComparer.Ordinal),
        Scopes = new HashSet<string>([WriteScope(resource)], StringComparer.Ordinal),
        RequiredConditions = [AbacConditions.TenantMatch],
        Sensitive = true,
    };

    /// <summary>Full bundle = platform defaults + the FHIR-façade rules. interop-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }
}
