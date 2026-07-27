namespace Mersal.Authz;

// Phase 20 — the unified patient profile (design 39). THE HIGHEST-RISK FEATURE IN THE PLATFORM: every other
// module is naturally scoped, this one deliberately aggregates everything about a patient onto one screen.
//
// The matrix below is design 39 §4 expressed as data rather than as branching inside a composer. That choice is
// the control: a role×section table can be read against the design doc line by line and asserted by a
// table-driven test over every cell, whereas the same rules spread across fifteen `if (role == …)` blocks are
// only ever verified by the cases somebody thought to write.
//
// Nothing here fetches. This file decides WHETHER a section may be fetched and WHICH projection of it the caller
// gets; the owning service still applies its own authorization to the call (39 §1: two independent layers,
// neither sufficient alone).

/// <summary>The 15 independently-gated section keys of the patient profile (design 39 §3), in render order.</summary>
public static class ProfileSections
{
    public const string Header = "header";
    public const string Alerts = "alerts";
    public const string Coverage = "coverage";
    public const string PastMedicalHistory = "pastMedicalHistory";
    public const string Encounters = "encounters";
    public const string Investigations = "investigations";
    public const string Prescriptions = "prescriptions";
    public const string Authorizations = "authorizations";
    public const string Referrals = "referrals";
    public const string Documents = "documents";
    public const string Notes = "notes";
    public const string Financial = "financial";
    public const string CaseManagement = "caseManagement";
    public const string Timeline = "timeline";
    public const string CallHistory = "callHistory";

    /// <summary>All 15 keys in design-39 §3 order — the order the profile renders in.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Header, Alerts, Coverage, PastMedicalHistory, Encounters, Investigations, Prescriptions,
        Authorizations, Referrals, Documents, Notes, Financial, CaseManagement, Timeline, CallHistory,
    ];

    /// <summary>The sections the patient context bar needs — the strip rendered on every clinical screen.</summary>
    public static readonly IReadOnlyList<string> ContextBar = [Header, Alerts];

    public static bool IsKnown(string key) => All.Contains(key, StringComparer.Ordinal);
}

/// <summary>
/// The four states a section can be in. <b>Restricted, Unavailable and NotApplicable are three different
/// answers</b> and are never collapsed: "you may not see this", "it broke", and "there is nothing here" lead a
/// user to three different next actions, and merging them turns a permissions problem into a clinical one.
/// </summary>
public enum ProfileSectionState
{
    /// <summary>Content present.</summary>
    Visible,
    /// <summary>The section exists; content withheld, with a reason and (where offered) a request-access action.</summary>
    Restricted,
    /// <summary>Nothing exists for this patient.</summary>
    NotApplicable,
    /// <summary>The owning service failed or timed out. NOT the same as empty.</summary>
    Unavailable,
}

/// <summary>Call-history projection depth (design 39 §5b). Ordered so a requested level can be CLAMPED to the
/// level the caller's role resolves to — a client may narrow, never widen.</summary>
public enum CallHistoryLevel
{
    /// <summary>Existence only — no rows.</summary>
    None = 0,
    /// <summary>Direction, date/time, reason code, outcome. No summary text.</summary>
    Meta = 1,
    /// <summary>+ duration, branch, summary, linked artefacts. No verification detail, no agent notes.</summary>
    Operational = 2,
    /// <summary>Every field, including verification detail and the agent who handled it.</summary>
    Full = 3,
}

/// <summary>
/// The facts the section matrix evaluates, resolved BEFORE evaluation exactly as treating-relationship and
/// case-assignment are resolved elsewhere in <see cref="AbacConditions"/>. The profile adds no new condition
/// types — design 39 §7.3: it is an intersection of the gates that already exist, never a union.
/// </summary>
public sealed record ProfileContext
{
    public required IReadOnlySet<string> Roles { get; init; }

    /// <summary>The caller has an active treating relationship with this beneficiary.</summary>
    public bool TreatingRelationship { get; init; }

    /// <summary>The caller holds an ACTIVE case assignment covering this beneficiary (unassignment revokes).</summary>
    public bool CaseAssignment { get; init; }

    /// <summary>The caller authored, or holds an active design-37 §6 release grant for, the sensitive material.
    /// The profile never sets this itself — it is reported by the owning service. See
    /// <see cref="SensitiveResultsExistenceOnly"/>.</summary>
    public bool SensitiveGrantActive { get; init; }

    /// <summary>A Passed caller-verification exists for the interaction in play (phase 15). Only meaningful for
    /// call-centre principals; see <see cref="RequiresCallCentreVerification"/>.</summary>
    public bool CallCentreVerified { get; init; }

    /// <summary>The caller's provider, for the own-orders / own-Rx row filters.</summary>
    public string? ProviderId { get; init; }
}

/// <summary>
/// One cell of the design-39 §4 matrix: what a role gets for a section, and what it degrades to when a required
/// condition fails.
///
/// <para><b>Degradation is the interesting half.</b> A non-treating doctor does not get "nothing" — they get
/// <see cref="ProfileSectionState.Restricted"/> with a reason, so they request access rather than conclude the
/// patient has no history (design 39 §7.5). A role with no entry at all gets the section omitted from the
/// response entirely — the `—` column, which is not the same as Restricted.</para>
/// </summary>
public sealed record SectionRule
{
    /// <summary>The state when every required condition holds.</summary>
    public required ProfileSectionState Granted { get; init; }

    /// <summary>The projection variant the owning provider must apply (e.g. <c>meta</c>, <c>own-orders</c>,
    /// <c>admin</c>). Null = the full projection for that section.</summary>
    public string? Variant { get; init; }

    /// <summary>How wide this cell is, used to resolve a principal holding several roles: the widest cell wins.
    /// Explicit rather than implied by declaration order, so widening a variant is a visible one-line change.</summary>
    public int Breadth { get; init; } = 100;

    /// <summary>ABAC conditions from <see cref="AbacConditions"/> that must hold. No new condition types.</summary>
    public IReadOnlyList<string> RequiredConditions { get; init; } = [];

    /// <summary>The state when a required condition fails. Restricted by default — existence, not silence.</summary>
    public ProfileSectionState Denied { get; init; } = ProfileSectionState.Restricted;

    /// <summary>Why it was withheld: <c>not-treating</c>, <c>not-assigned</c>, <c>role-not-permitted</c>,
    /// <c>sensitive-requires-grant</c>. Surfaced to the user so the withholding is legible.</summary>
    public string? DeniedReason { get; init; }
}

/// <summary>The resolved answer for one section: what the composer should do with it.</summary>
public sealed record SectionDecision(string Key, ProfileSectionState State, string? Variant, string? ReasonCode)
{
    /// <summary>Whether the owning service should be called at all. A section that can never be visible is not
    /// fetched — cheaper, and it leaks nothing to the owning service either (design 39 build prompt 20.1).</summary>
    public bool ShouldFetch => State == ProfileSectionState.Visible;
}

/// <summary>Reason codes for a withheld section. Kept as constants because the SPA renders a distinct message —
/// and, for <see cref="SensitiveRequiresGrant"/>, a "Request access" action — per code.</summary>
public static class ProfileReasons
{
    public const string NotTreating = "not-treating";
    public const string NotAssigned = "not-assigned";
    public const string RoleNotPermitted = "role-not-permitted";
    public const string SensitiveRequiresGrant = "sensitive-requires-grant";
}

/// <summary>Projection variants, named so the providers and the tests share one vocabulary.</summary>
public static class ProfileVariants
{
    public const string Min = "min";
    public const string Meta = "meta";
    public const string Admin = "admin";
    public const string Financial = "financial";
    public const string Summary = "summary";
    public const string Status = "status";
    public const string Cost = "cost";
    public const string Amounts = "amounts";
    public const string Allergy = "allergy";
    public const string PharmacyLimit = "pharmacy-limit";
    public const string OwnOrders = "own-orders";
    public const string OwnRx = "own-rx";
    public const string Access = "access";
    public const string Full = "full";
    public const string Operational = "operational";
}

/// <summary>
/// Design 39 §4 as executable policy: which sections each role may see, in which projection, under which
/// existing conditions — plus the coarse <see cref="PolicyRule"/>s profile-service authorizes with.
/// </summary>
public static class ProfilePolicies
{
    public const string Version = "20.0";

    public const string Resource = "patient-profile";
    public const string PhotoResource = "patient-photo";

    /// <summary>Open a patient profile (a PHI read — audited on every open, naming the sections served).</summary>
    public const string Read = "profile:read";
    /// <summary>Generate the role-projected print/export summary (audited as a PHI export, separately).</summary>
    public const string Export = "profile:export";
    /// <summary>Retrieve the beneficiary photo — a narrower allow-list than the profile itself (design 39 §5).</summary>
    public const string Photo = "profile:photo";
    /// <summary>Read the call-history section's rows from callcentre-service.</summary>
    public const string CallHistoryRead = "callcentre:history:read";

    // ---------------------------------------------------------------- the matrix (design 39 §4)

    private static SectionRule Vis(string? variant = null, int breadth = 100) =>
        new() { Granted = ProfileSectionState.Visible, Variant = variant, Breadth = breadth };

    /// <summary>An existence-only cell: the role knows the section exists and may ask for access.</summary>
    private static SectionRule Res(string reason = ProfileReasons.RoleNotPermitted) =>
        new()
        {
            Granted = ProfileSectionState.Restricted, Breadth = 10,
            Denied = ProfileSectionState.Restricted, DeniedReason = reason,
        };

    /// <summary>Visible while treating; Restricted with <c>not-treating</c> otherwise. This one rule is why a
    /// doctor appears twice in design 39 §4 — same role, two rows, one condition.</summary>
    private static SectionRule Treating(string? variant = null, int breadth = 100) =>
        new()
        {
            Granted = ProfileSectionState.Visible, Variant = variant, Breadth = breadth,
            RequiredConditions = [AbacConditions.TreatingRelationship],
            Denied = ProfileSectionState.Restricted, DeniedReason = ProfileReasons.NotTreating,
        };

    /// <summary>Visible while an ACTIVE case assignment covers the beneficiary; Restricted otherwise.</summary>
    private static SectionRule Assigned(string? variant = null, int breadth = 100) =>
        new()
        {
            Granted = ProfileSectionState.Visible, Variant = variant, Breadth = breadth,
            RequiredConditions = [AbacConditions.CaseAssignment],
            Denied = ProfileSectionState.Restricted, DeniedReason = ProfileReasons.NotAssigned,
        };

    private static Dictionary<string, SectionRule> Row(params (string Section, SectionRule Rule)[] cells)
    {
        var row = new Dictionary<string, SectionRule>(StringComparer.Ordinal);
        foreach (var (section, rule) in cells) row[section] = rule;
        return row;
    }

    // Reception — the front desk. Identity, alerts, coverage, visit LOGISTICS and operational history. No clinical
    // section is even fetched: reception ≠ EMR is the oldest rule in the platform (11-permission-matrix).
    private static Dictionary<string, SectionRule> Reception() => Row(
        (ProfileSections.Header, Vis()),
        (ProfileSections.Alerts, Vis()),
        (ProfileSections.Coverage, Vis()),
        (ProfileSections.Encounters, Vis(ProfileVariants.Meta, 50)),
        (ProfileSections.Authorizations, Vis(ProfileVariants.Status, 40)),
        (ProfileSections.Referrals, Vis()),
        (ProfileSections.Documents, Res()),
        (ProfileSections.Notes, Vis(ProfileVariants.Admin, 50)),
        (ProfileSections.Timeline, Vis(ProfileVariants.Admin, 50)),
        (ProfileSections.CallHistory, Vis(ProfileVariants.Operational, 60)));

    // Call Centre — reception's sections plus FULL call history, because the contact log is their own record of
    // work. The verification gate (phase 15) still stands in front of all of it.
    private static Dictionary<string, SectionRule> CallCentre()
    {
        var row = Reception();
        row[ProfileSections.CallHistory] = Vis(ProfileVariants.Full);
        return row;
    }

    // Doctor / nurse — everything clinical, CONDITIONAL ON TREATING. Never the financial section: a clinician does
    // not need to know what the visit cost, and 11-permission-matrix keeps money out of the clinical zone.
    private static Dictionary<string, SectionRule> Clinician() => Row(
        (ProfileSections.Header, Vis()),
        (ProfileSections.Alerts, Vis()),
        (ProfileSections.Coverage, Treating()),
        (ProfileSections.PastMedicalHistory, Treating()),
        (ProfileSections.Encounters, Treating()),
        (ProfileSections.Investigations, Treating()),
        (ProfileSections.Prescriptions, Treating()),
        (ProfileSections.Authorizations, Treating()),
        (ProfileSections.Referrals, Treating()),
        (ProfileSections.Documents, Treating()),
        (ProfileSections.Notes, Treating()),
        (ProfileSections.CaseManagement, Treating()),
        (ProfileSections.Timeline, Treating()),
        (ProfileSections.CallHistory, Treating(ProfileVariants.Operational, 60)));

    // Lab / imaging — identity enough to label a specimen, allergies because they affect contrast and reagents,
    // and ITS OWN ORDERS. Nothing else exists for this role: no coverage, no prescriptions, no results but its own.
    private static Dictionary<string, SectionRule> Diagnostics() => Row(
        (ProfileSections.Header, Vis(ProfileVariants.Min, 50)),
        (ProfileSections.Alerts, Vis(ProfileVariants.Allergy, 50)),
        (ProfileSections.Investigations, Vis(ProfileVariants.OwnOrders, 50)));

    // Pharmacy — identity, allergies (drug-allergy checking is the point), the pharmacy limit so they can tell a
    // member why a dispense is short, and ITS OWN prescriptions. Never investigation results.
    private static Dictionary<string, SectionRule> Pharmacy() => Row(
        (ProfileSections.Header, Vis(ProfileVariants.Min, 50)),
        (ProfileSections.Alerts, Vis(ProfileVariants.Allergy, 50)),
        (ProfileSections.Coverage, Vis(ProfileVariants.PharmacyLimit, 40)),
        (ProfileSections.Prescriptions, Vis(ProfileVariants.OwnRx, 50)));

    // Medical approval — standing clinical oversight WITHOUT a treating relationship, which is exactly why the
    // sensitive-result gate is called out separately: 39 §4 note * keeps restricted results existence-only even
    // here until a 37 §6 grant exists. The profile must not become the shortcut around that.
    private static Dictionary<string, SectionRule> MedicalApproval() => Row(
        (ProfileSections.Header, Vis()),
        (ProfileSections.Alerts, Vis()),
        (ProfileSections.Coverage, Vis()),
        (ProfileSections.PastMedicalHistory, Vis()),
        (ProfileSections.Encounters, Vis()),
        (ProfileSections.Investigations, Vis()),
        (ProfileSections.Prescriptions, Vis()),
        (ProfileSections.Authorizations, Vis()),
        (ProfileSections.Referrals, Vis()),
        (ProfileSections.Documents, Vis()),
        (ProfileSections.Notes, Vis()),
        (ProfileSections.CaseManagement, Vis()),
        (ProfileSections.Timeline, Vis()),
        (ProfileSections.CallHistory, Vis(ProfileVariants.Operational, 60)));

    // Medical Director — the approval team's reach plus a FINANCIAL SUMMARY (not the claim detail) and full call
    // history. The only clinical role that sees money at all, and only in aggregate.
    private static Dictionary<string, SectionRule> MedicalDirector()
    {
        var row = MedicalApproval();
        row[ProfileSections.Financial] = Vis(ProfileVariants.Summary, 60);
        row[ProfileSections.CallHistory] = Vis(ProfileVariants.Full);
        return row;
    }

    // Case manager — coordination, gated on an ACTIVE assignment. Investigations and prescriptions stay
    // existence-only: coordinating care needs to know a test happened, not what it said.
    private static Dictionary<string, SectionRule> CaseManager() => Row(
        (ProfileSections.Header, Vis()),
        (ProfileSections.Alerts, Vis()),
        (ProfileSections.Coverage, Assigned()),
        (ProfileSections.PastMedicalHistory, Assigned(ProfileVariants.Summary, 60)),
        (ProfileSections.Encounters, Assigned()),
        (ProfileSections.Investigations, Res()),
        (ProfileSections.Prescriptions, Res()),
        (ProfileSections.Authorizations, Assigned()),
        (ProfileSections.Referrals, Assigned()),
        (ProfileSections.Documents, Assigned(ProfileVariants.Admin, 50)),
        (ProfileSections.Notes, Assigned()),
        (ProfileSections.CaseManagement, Assigned()),
        (ProfileSections.Timeline, Assigned()),
        (ProfileSections.CallHistory, Assigned(ProfileVariants.Full)));

    // Finance / claims — amounts, cost-share, settlement. NO alerts (an allergy is not a billing fact), NO
    // diagnosis anywhere, NO photo (see ProfilePhotoAccess), and call history at META: enough to see a billing
    // call happened, never the narrative.
    private static Dictionary<string, SectionRule> Finance() => Row(
        (ProfileSections.Header, Vis(ProfileVariants.Min, 50)),
        (ProfileSections.Coverage, Vis(ProfileVariants.Amounts, 50)),
        (ProfileSections.Encounters, Vis(ProfileVariants.Meta, 50)),
        (ProfileSections.Authorizations, Vis(ProfileVariants.Cost, 50)),
        (ProfileSections.Documents, Res()),
        (ProfileSections.Notes, Vis(ProfileVariants.Financial, 40)),
        (ProfileSections.Financial, Vis()),
        (ProfileSections.Timeline, Vis(ProfileVariants.Financial, 40)),
        (ProfileSections.CallHistory, Vis(ProfileVariants.Meta, 30)));

    // Beneficiary management — the membership record. Past medical history is existence-only: they file it, they
    // do not read it.
    private static Dictionary<string, SectionRule> BeneficiaryMgmt() => Row(
        (ProfileSections.Header, Vis()),
        (ProfileSections.Alerts, Vis()),
        (ProfileSections.Coverage, Vis()),
        (ProfileSections.PastMedicalHistory, Res()),
        (ProfileSections.Encounters, Vis(ProfileVariants.Meta, 50)),
        (ProfileSections.Authorizations, Vis(ProfileVariants.Status, 40)),
        (ProfileSections.Referrals, Vis()),
        (ProfileSections.Documents, Vis(ProfileVariants.Admin, 50)),
        (ProfileSections.Notes, Vis()),
        (ProfileSections.Timeline, Vis(ProfileVariants.Admin, 50)),
        (ProfileSections.CallHistory, Vis(ProfileVariants.Full)));

    // Org / super admin — administers ACCESS, not records. A minimal header so a support ticket can be matched to
    // a member, and the ACCESS timeline (who looked at this patient). No clinical, benefit or financial section.
    private static Dictionary<string, SectionRule> PlatformAdmin() => Row(
        (ProfileSections.Header, Vis(ProfileVariants.Min, 50)),
        (ProfileSections.Timeline, Vis(ProfileVariants.Access, 30)));

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, SectionRule>> Matrix =
        new Dictionary<string, IReadOnlyDictionary<string, SectionRule>>(StringComparer.Ordinal)
        {
            ["reception"] = Reception(),
            ["call_center"] = CallCentre(),
            ["call_center_supervisor"] = CallCentre(),
            ["doctor"] = Clinician(),
            ["nurse"] = Clinician(),
            ["lab_tech"] = Diagnostics(),
            ["imaging_tech"] = Diagnostics(),
            ["pharmacist"] = Pharmacy(),
            ["pharmacy_supervisor"] = Pharmacy(),
            ["medical_approval"] = MedicalApproval(),
            ["approvals_team"] = MedicalApproval(),
            ["medical_director"] = MedicalDirector(),
            ["case_manager"] = CaseManager(),
            ["finance"] = Finance(),
            ["finance_approver"] = Finance(),
            ["claims_officer"] = Finance(),
            ["claims_reviewer"] = Finance(),
            ["beneficiary_mgmt"] = BeneficiaryMgmt(),
            ["beneficiary_mgmt_supervisor"] = BeneficiaryMgmt(),
            ["org_admin"] = PlatformAdmin(),
            ["super_admin"] = PlatformAdmin(),
        };

    /// <summary>Every role the matrix names — the domain of the table-driven matrix test.</summary>
    public static IReadOnlyCollection<string> KnownRoles => (IReadOnlyCollection<string>)Matrix.Keys;

    // ---------------------------------------------------------------- resolution

    /// <summary>
    /// Resolve one section for one caller. Returns <c>null</c> when the section is not returned AT ALL for this
    /// caller (the `—` column of design 39 §4) — the composer omits the key entirely rather than emitting an
    /// empty shell, because a role that can never see a section has no access to request.
    /// </summary>
    /// <remarks>
    /// A principal holding several roles gets the WIDEST cell across them. That is ordinary RBAC union over role
    /// grants and matches <see cref="FieldAccessMatrix.ReadableClasses"/>. It does not widen the ABAC gates: the
    /// conditions on the winning cell still have to hold, which is what design 39 §7.3 means by "intersection".
    /// </remarks>
    public static SectionDecision? Decide(string section, ProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SectionDecision? best = null;
        var bestBreadth = int.MinValue;

        foreach (var role in context.Roles)
        {
            if (!Matrix.TryGetValue(role, out var row) || !row.TryGetValue(section, out var rule)) continue;

            var satisfied = rule.RequiredConditions.All(c => Holds(c, context));
            var candidate = satisfied
                ? new SectionDecision(section, rule.Granted, rule.Variant, null)
                : new SectionDecision(section, rule.Denied, null, rule.DeniedReason);

            // Widest wins: a Visible cell beats a Restricted one; among equals, the broader variant.
            var weight = (Rank(candidate.State) * 1000) + rule.Breadth;
            if (weight <= bestBreadth) continue;
            bestBreadth = weight;
            best = candidate;
        }

        return best;
    }

    /// <summary>Resolve every section, in design-39 §3 render order. Sections with no cell are omitted.</summary>
    public static IReadOnlyList<SectionDecision> DecideAll(ProfileContext context) =>
        [.. ProfileSections.All.Select(s => Decide(s, context)).OfType<SectionDecision>()];

    private static int Rank(ProfileSectionState state) => state switch
    {
        ProfileSectionState.Visible => 3,
        ProfileSectionState.Restricted => 2,
        ProfileSectionState.NotApplicable => 1,
        _ => 0,
    };

    private static bool Holds(string condition, ProfileContext c) => condition switch
    {
        AbacConditions.TreatingRelationship => c.TreatingRelationship,
        AbacConditions.CaseAssignment => c.CaseAssignment,
        AbacConditions.ProviderOwnership => c.ProviderId is not null,
        _ => false, // unknown condition → not satisfied (default-deny), mirroring the engine
    };

    // ---------------------------------------------------------------- cross-cutting gates

    /// <summary>
    /// Design 39 §4 note *: sensitive results stay EXISTENCE-ONLY even for the approval team and the medical
    /// director until a design-37 §6 grant exists. The profile never widens this — it returns whether the caller
    /// still needs a grant, and the owning service (which is the only thing that knows a result's sensitivity)
    /// applies <see cref="SensitiveDisclosure"/> as it always has.
    /// </summary>
    public static bool SensitiveResultsExistenceOnly(ProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !context.SensitiveGrantActive;
    }

    /// <summary>Whether this caller must clear the phase-15 caller-verification gate before ANY section is served.
    /// True for call-centre principals; the profile consumes that gate, it does not re-implement it.</summary>
    public static bool RequiresCallCentreVerification(IReadOnlySet<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Contains("call_center") || roles.Contains("call_center_supervisor");
    }

    /// <summary>The call-history depth this caller resolves to, derived from the SAME matrix cell that decides the
    /// section — so the two can never disagree.</summary>
    public static CallHistoryLevel CallHistoryLevelFor(ProfileContext context)
    {
        var decision = Decide(ProfileSections.CallHistory, context);
        if (decision is null || decision.State != ProfileSectionState.Visible) return CallHistoryLevel.None;
        return decision.Variant switch
        {
            ProfileVariants.Meta => CallHistoryLevel.Meta,
            ProfileVariants.Operational => CallHistoryLevel.Operational,
            _ => CallHistoryLevel.Full,
        };
    }

    /// <summary>
    /// Clamp a client-supplied level to what the caller's role allows. A client may NARROW (a supervisor asking
    /// for meta gets meta) but never widen — which is why this is a clamp rather than a validation: rejecting the
    /// request would tell the caller what they are not allowed to have, and honouring it would be the bug.
    /// </summary>
    public static CallHistoryLevel Clamp(CallHistoryLevel requested, CallHistoryLevel allowed) =>
        requested < allowed ? requested : allowed;

    // ---------------------------------------------------------------- coarse rules

    /// <summary>Every role the matrix names — the coarse RBAC gate. Section-level shaping is the second layer.</summary>
    private static readonly HashSet<string> ProfileReaders = [.. Matrix.Keys];

    /// <summary>
    /// The roles whose matrix row actually HAS a call-history cell — derived from the matrix rather than
    /// re-listed.
    ///
    /// <para>Granting <c>callcentre:history:read</c> to every profile reader would hand the endpoint to labs,
    /// pharmacies and platform admins, whose rows have no callHistory cell at all: a scope that returns nothing
    /// today, and the sort of scope something else gets wired to later precisely because it looks harmless. The
    /// scope-integrity test in libs/authz caught exactly this when the rule was written by hand.</para>
    /// </summary>
    private static readonly HashSet<string> CallHistoryReaders =
        [.. Matrix.Where(row => row.Value.ContainsKey(ProfileSections.CallHistory)).Select(row => row.Key)];

    /// <summary>The profile rules on their own (spliceable into a service's bundle).</summary>
    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // Opening a profile is a PHI read on an aggregate — sensitive by definition, so every allow is audited
        // too, not just every deny.
        new PolicyRule
        {
            Action = Read, ResourceType = Resource,
            Roles = ProfileReaders, Scopes = Set("profile:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // The print/export summary is a PHI EXPORT: narrower roles, separate audit, never a superset of the screen.
        new PolicyRule
        {
            Action = Export, ResourceType = Resource,
            Roles = Set("doctor", "nurse", "medical_approval", "approvals_team", "medical_director",
                        "case_manager", "beneficiary_mgmt", "beneficiary_mgmt_supervisor"),
            Scopes = Set("profile:export"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // The photo allow-list is NARROWER than the profile's: identification need, not curiosity (design 39 §5).
        new PolicyRule
        {
            Action = Photo, ResourceType = PhotoResource,
            Roles = Set([.. ProfilePhotoAccess.AllowedRoles]), Scopes = Set("profile:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
        // The call-history read, served by callcentre-service to the profile's provider.
        new PolicyRule
        {
            Action = CallHistoryRead, ResourceType = CallCentrePolicies.Resource,
            Roles = CallHistoryReaders, Scopes = Set("callcentre:history:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + the profile rules. profile-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}

/// <summary>
/// Design 39 §5 — the beneficiary photo is identity-sensitive, biometric-adjacent data for a refugee population,
/// not a decoration.
///
/// <para>Its allow-list is deliberately NARROWER than the header section that carries it: a role sees the photo
/// only where mis-identifying a person is the actual risk being managed — the front desk, the phone, the
/// consulting room, the membership office. Finance, claims, labs, pharmacies and platform admins get a header
/// with <b>no photo field at all</b>; there is nothing for them to fail to render.</para>
/// </summary>
public static class ProfilePhotoAccess
{
    /// <summary>Roles with a legitimate identification need (design 39 §5).</summary>
    public static readonly IReadOnlyList<string> AllowedRoles =
    [
        "reception", "call_center", "call_center_supervisor", "doctor", "nurse",
        "medical_approval", "approvals_team", "medical_director", "case_manager",
        "beneficiary_mgmt", "beneficiary_mgmt_supervisor",
    ];

    /// <summary>Whether the header section may carry a photo reference for this caller.</summary>
    public static bool MayView(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(r => AllowedRoles.Contains(r, StringComparer.Ordinal));
    }
}
