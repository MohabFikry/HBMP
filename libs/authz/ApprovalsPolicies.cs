namespace Mersal.Authz;

/// <summary>
/// The medical-approvals policy overlay (phase 7). The Medical Approval team and Medical Director review
/// authorization requests. Reads are tenant-scoped OVERSIGHT (no treating relationship — the approver did not
/// treat the patient, 11-permission-matrix §3.2): the worklist projection carries no clinical payload, while the
/// review DTO (the ONLY clinical-context endpoint) is field-scoped and PHI-read audited under purpose PUR.
/// Break-glass paths (emergency approve / director override / manual authorization) are Director-only and each
/// requires extra justification (enforced in the handler) with a specially-flagged audit trail. approvals-service
/// authorizes with <see cref="Bundle"/>. See 18-security-model.md §4, 19-audit-strategy.md, 23-state-machines §5.
/// </summary>
public static class ApprovalsPolicies
{
    public const string Version = "7.0";

    /// <summary>Reviewer inbox / worklist read — min-necessary projection, no clinical fields.</summary>
    public const string List = "auth:list";
    /// <summary>Pick up a Submitted request (Submitted → UnderReview), starting the SLA timer.</summary>
    public const string Assign = "auth:assign";
    /// <summary>The clinical review view — EMR summary + notes + supporting documents, field-scoped, PHI-audited.</summary>
    public const string Review = "auth:review";
    /// <summary>Decide (approve / partial / reject / request-info / resupply) — phase 7.2.</summary>
    public const string Decide = "auth:decide";
    /// <summary>Emergency fast-track (Submitted → EmergencyApproved), Director only — phase 7.3.</summary>
    public const string Emergency = "auth:emergency";
    /// <summary>Director override of a rejection (Rejected → Overridden), Director only — phase 7.3.</summary>
    public const string Override = "auth:override";
    /// <summary>Manual authorization created without a provider submission — phase 7.3.</summary>
    public const string Manual = "auth:manual";
    /// <summary>
    /// Author the approvals engine's routing and SLA rules (ADR-0035 §5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately SEPARATE from <see cref="Decide"/>. Deciding one case and authoring the rule that shapes a
    /// thousand are different powers, and a reviewer who could edit the rule routing their own work could
    /// route it away from themselves — a change that would look like ordinary configuration rather than like
    /// avoiding a decision. <c>medical_approval</c> holds Decide and NOT this.
    /// </para>
    /// <para>
    /// The first families are routing and SLA, which change WHO decides and BY WHEN, never WHAT is decided.
    /// Nothing this action grants can approve or refuse anything.
    /// </para>
    /// </remarks>
    public const string Configure = "auth:configure";
    /// <summary>
    /// Ask for an EXPIRED prescription or investigation order to be made actionable again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately its own action with its own scope rather than a share of <see cref="Manual"/>. The people
    /// who hold it — pharmacists, lab and imaging technicians — must be able to raise exactly one shape of
    /// request about exactly one expired item they are already holding, and nothing else. <c>auth:manual</c>
    /// would have let a pharmacist author an arbitrary authorization for any beneficiary and any service
    /// code; <c>auth:ingest</c> is the machine seam and would have handed them the routing saga's reach.
    /// </para>
    /// <para>
    /// It carries NO decision authority. The request lands Submitted in the approval team's queue like every
    /// other, and the requester cannot decide their own.
    /// </para>
    /// </remarks>
    public const string RequestExtension = "auth:request-extension";

    /// <summary>
    /// A lab / imaging technician asking whether another examination may stand in for the one ordered
    /// (ADR-0034 Decision 4).
    /// </summary>
    /// <remarks>
    /// <para>Deliberately a REQUEST rather than a choice. The pharmacy counter substitutes from the drug's
    /// ATC-5 class — a real equivalence set held in master data — and the server refuses anything outside it.
    /// Examinations have no equivalence set anywhere, so a list would have to be derived from the category,
    /// which would put "any radiology procedure" behind a button.</para>
    /// <para>Pharmacists are NOT granted it: they already have the formulary path, and the pharmacy service
    /// already routes an off-formulary request to approvals by itself. Two ways to ask is two answers to keep
    /// in step.</para>
    /// </remarks>
    public const string RequestSubstitution = "auth:request-substitution";

    /// <summary>
    /// Complete the post-hoc review of a break-glass decision (US-061/062, 23-state-machines §5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT held by <c>medical_approval</c>, who hold <see cref="Manual"/>. Segregation of duties
    /// on this control is enforced twice: per-person in the handler, which refuses a reviewer who is the
    /// break-glass actor, and per-ROLE here. Without the second, the team that raises manual authorizations
    /// would also be the class that signs them off — colleagues reviewing each other's overrides, which is the
    /// arrangement this control exists to replace, only with a timestamp on it.
    /// </para>
    /// <para>
    /// It carries no decision authority over the authorization itself. A review concluding NotJustified is a
    /// FINDING; the care was already delivered and nothing here can unwind it.
    /// </para>
    /// </remarks>
    public const string Retrospective = "auth:retrospective";

    public const string Resource = "authorization";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // Worklist inbox — tenant-scoped, no clinical payload → not flagged sensitive.
        new PolicyRule
        {
            Action = List, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // Pick up a request — state change only, tenant-scoped.
        new PolicyRule
        {
            Action = Assign, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:review"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // The clinical review view — tenant-scoped oversight read; PHI, so audited even on allow (purpose PUR).
        new PolicyRule
        {
            Action = Review, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:review"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Decisions (phase 7.2) — tenant-scoped; the decision writes an immutable rationale-bearing record.
        new PolicyRule
        {
            Action = Decide, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:decide"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Break-glass: emergency approval — Director authority only (phase 7.3).
        new PolicyRule
        {
            Action = Emergency, ResourceType = Resource,
            Roles = Set("medical_director"), Scopes = Set("auth:emergency"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Break-glass: director override of a rejection — Director authority only (phase 7.3).
        new PolicyRule
        {
            Action = Override, ResourceType = Resource,
            Roles = Set("medical_director"), Scopes = Set("auth:override"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Engine authoring — the supervisor who absorbs the consequence of a bad rule, and Super Admin.
        // NOT medical_approval: see the note on Configure.
        new PolicyRule
        {
            Action = Configure, ResourceType = Resource,
            Roles = Set("medical_director", "super_admin"), Scopes = Set("auth:configure"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Manual authorization — Medical Approval / Director (phase 7.3).
        new PolicyRule
        {
            Action = Manual, ResourceType = Resource,
            Roles = Set("medical_approval", "medical_director"), Scopes = Set("auth:manual"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Completing the post-hoc review of a break-glass decision. Director + Super Admin, NOT the approval
        // team who raise manual authorizations — see the note on Retrospective for why the role split matters
        // on top of the per-person SoD the handler enforces.
        new PolicyRule
        {
            Action = Retrospective, ResourceType = Resource,
            Roles = Set("medical_director", "super_admin"), Scopes = Set("auth:retrospective"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Ask for an expired item to be revalidated. The FULFILLERS hold this — the people standing at a
        // counter with the patient and the lapsed document — not the approval team, who decide it.
        new PolicyRule
        {
            Action = RequestExtension, ResourceType = Resource,
            Roles = Set("pharmacist", "lab_tech", "imaging_tech", "radiology_tech"), Scopes = Set("auth:request-extension"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // Ask whether another examination may stand in. The BENCH holds this, and only the bench: the
        // pharmacy counter resolves the same question against a real formulary without asking anyone.
        new PolicyRule
        {
            Action = RequestSubstitution, ResourceType = Resource,
            Roles = Set("lab_tech", "imaging_tech", "radiology_tech"), Scopes = Set("auth:request-substitution"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + the approvals rules. approvals-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
