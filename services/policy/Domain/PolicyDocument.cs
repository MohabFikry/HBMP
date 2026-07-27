namespace Mersal.Policy.Domain;

// Phase 19.3b — documents on policy and member (design 38 §5b). Bytes live in document-service/MinIO; this is
// the linkage, the classification and the lifecycle.

/// <summary>What kind of document this is. The first six are policy paperwork; the rest hang off a member.
/// This decides the DEFAULT visibility — see <see cref="DocumentClassification"/>.</summary>
public enum DocumentClass
{
    // Policy scope
    PolicyContract, BenefitSchedule, PayerAgreement, Endorsement, FinancialGuarantee, PolicyCorrespondence,
    // Member scope
    IdentityDocument, ProofOfEligibility, EnrolmentForm, ConsentForm,
    PastMedicalHistory, MedicalReport, LabResult, Prescription, DischargeSummary,
    Referral, InvoiceReceipt, MemberCorrespondence, Other,
    /// <summary>Phase 20.3 — the beneficiary's identification photograph (design 39 §5). Administrative by
    /// visibility, but with its OWN, much narrower role allow-list: see <see cref="DocumentAccess"/>.</summary>
    IdentityPhoto,
}

/// <summary>
/// The design-37 §5 sensitive categories.
///
/// <para>This resolves a gap in the build prompt's classification rule. It says "anything mental-health,
/// HIV/STI, genetic, substance-use, reproductive or GBV-related → Restricted" — but none of those is a
/// document class: they are properties of the CONTENT of a MedicalReport or a LabResult. Without a field for
/// them the rule is unimplementable, and every such document would quietly default to merely Clinical, which
/// is the exact material design 37 §6 exists to keep out of ordinary clinical reach.</para>
/// </summary>
public enum SensitiveCategory { MentalHealth, HivSti, Genetic, SubstanceUse, Reproductive, Gbv }

public enum DocumentLinkStatus { Active, Superseded, Withdrawn }

/// <summary>
/// A document attached to a policy or a member.
///
/// <para><b>Two date fields, deliberately.</b> <see cref="DocumentDate"/> is the date ON the document;
/// <see cref="UploadedAt"/> is when it reached us. Past medical history is read in CLINICAL order — a
/// discharge summary from 2019 scanned in today belongs in 2019 on the member's history, not at the top.
/// Sorting by upload order would make a member's history read backwards.</para>
/// </summary>
public sealed class PolicyDocument
{
    public Guid LinkId { get; set; }
    public string TenantId { get; set; } = "";
    public NoteScope Scope { get; set; }
    public Guid ScopeRef { get; set; }

    /// <summary>document-service reference — a value, never a cross-schema FK. The bytes, the checksum and the
    /// malware scan all live there.</summary>
    public Guid DocumentId { get; set; }

    public int VersionNo { get; set; } = 1;
    public Guid? SupersedesLinkId { get; set; }

    public DocumentClass DocumentClass { get; set; }
    public NoteVisibility VisibilityClass { get; set; }
    public SensitiveCategory? SensitiveCategory { get; set; }

    public string Title { get; set; } = default!;
    public string? Description { get; set; }
    /// <summary>The date ON the document — distinct from <see cref="UploadedAt"/>. See the type summary.</summary>
    public DateOnly? DocumentDate { get; set; }
    public string? IssuingProvider { get; set; }

    public Guid UploadedByUserId { get; set; }
    public string UploadedByUsername { get; set; } = default!;
    public string UploadedByDisplay { get; set; } = default!;
    public DateTimeOffset UploadedAt { get; set; }

    public DocumentLinkStatus Status { get; set; } = DocumentLinkStatus.Active;
    public Guid? WithdrawnByUserId { get; set; }
    public string? WithdrawnByUsername { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
    public string? WithdrawalReason { get; set; }

    public DateOnly? ExpiresOn { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public string? VerifiedByUsername { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? VerificationNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsExpired(DateOnly today) => ExpiresOn is { } expiry && today > expiry;

    /// <summary>Every download is audited, whatever the class — but clinical and restricted material is PHI and
    /// carries the higher severity a review filters on.</summary>
    public bool IsPhi => VisibilityClass is NoteVisibility.Clinical or NoteVisibility.Restricted;
}

/// <summary>
/// Phase 19.3b — <b>document_class drives a DEFAULT visibility that the uploader may RAISE but never LOWER</b>.
///
/// This is the point of the feature. Classification cannot be left to the uploader's judgement alone, because
/// the failure is silent: a past medical history filed as Administrative is readable by finance and the call
/// centre forever, and nothing about it looks wrong. So the class decides a floor, and a human may only ever
/// tighten it.
/// </summary>
public static class DocumentClassification
{
    private static readonly HashSet<DocumentClass> Clinical =
    [
        DocumentClass.PastMedicalHistory, DocumentClass.MedicalReport, DocumentClass.LabResult,
        DocumentClass.Prescription, DocumentClass.DischargeSummary, DocumentClass.Referral,
    ];

    /// <summary>The floor for a class, before any sensitive category is considered.</summary>
    public static NoteVisibility DefaultFor(DocumentClass documentClass) => documentClass switch
    {
        _ when Clinical.Contains(documentClass) => NoteVisibility.Clinical,
        DocumentClass.InvoiceReceipt or DocumentClass.FinancialGuarantee => NoteVisibility.Financial,
        _ => NoteVisibility.Administrative,
    };

    /// <summary>
    /// The floor including the sensitive category. A declared 37 §5 category forces <c>Restricted</c> whatever
    /// the class says — mental-health, HIV/STI, genetic, substance-use, reproductive and GBV material is
    /// existence-only until released through the §6 grant flow, and that must not be reachable by filing it
    /// under a milder class.
    /// </summary>
    public static NoteVisibility DefaultFor(DocumentClass documentClass, SensitiveCategory? category) =>
        category is not null ? NoteVisibility.Restricted : DefaultFor(documentClass);

    /// <summary>Rank, so "raise" and "lower" are well defined. Mirrors <c>policy.note_visibility_rank</c>.</summary>
    public static int Rank(NoteVisibility visibility) => visibility switch
    {
        NoteVisibility.Administrative => 1,
        NoteVisibility.Financial => 2,
        NoteVisibility.Clinical => 3,
        NoteVisibility.Restricted => 4,
        _ => 0,
    };

    /// <summary>
    /// Resolve the visibility an upload should be stored with.
    /// </summary>
    /// <param name="requested">What the uploader asked for; null = take the default.</param>
    /// <returns>The resolved visibility, or null when the uploader tried to LOWER it below the class floor —
    /// which the caller turns into a 422 naming both values, rather than silently applying the floor. Silently
    /// correcting them would teach uploaders that the field does nothing.</returns>
    public static NoteVisibility? Resolve(
        DocumentClass documentClass, SensitiveCategory? category, NoteVisibility? requested)
    {
        var floor = DefaultFor(documentClass, category);
        if (requested is not { } asked) return floor;
        return Rank(asked) < Rank(floor) ? null : asked;
    }
}

/// <summary>
/// Who may DOWNLOAD a document's bytes (design 38 §5b "access").
///
/// <para>Listing and downloading are separate authorities, and that separation is the control. Everyone
/// entitled to the member's record may see that a document EXISTS — class, title, date, uploader, status —
/// because a record that looks empty sends an officer away believing nothing was filed. Retrieving the CONTENT
/// is a narrower, always-audited act.</para>
/// </summary>
public static class DocumentAccess
{
    private static readonly Dictionary<NoteVisibility, string[]> Downloaders = new()
    {
        [NoteVisibility.Administrative] =
        [
            "beneficiary_mgmt", "beneficiary_mgmt_supervisor", "policy_admin", "org_admin", "super_admin",
            "finance", "claims_officer", "call_center", "medical_approval", "doctor", "nurse", "reception",
            "case_manager", "medical_director",
        ],
        [NoteVisibility.Financial] =
        [
            "finance", "claims_officer", "beneficiary_mgmt", "beneficiary_mgmt_supervisor",
            "policy_admin", "org_admin", "super_admin", "medical_director",
        ],
        // NOT finance, NOT claims, NOT reception, NOT the call centre — the hard rule from 11-permission-matrix
        // that a note is not an exception to, and neither is a scanned lab result.
        [NoteVisibility.Clinical] =
        [
            "doctor", "nurse", "medical_approval", "medical_director", "case_manager", "super_admin",
        ],
    };

    /// <summary>Whether this caller may fetch the bytes.</summary>
    /// <param name="hasSensitiveGrant">The design-37 §6 fact: the caller is the authoring clinician or holds an
    /// active release grant. The ONLY route to a Restricted document.</param>
    public static bool MayDownload(
        NoteVisibility visibility, IReadOnlyCollection<string> roles, bool hasSensitiveGrant = false)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (visibility == NoteVisibility.Restricted) return hasSensitiveGrant;
        return Downloaders.TryGetValue(visibility, out var allowed)
               && roles.Any(r => allowed.Contains(r, StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether this caller may fetch the bytes, taking the document CLASS into account as well as its
    /// visibility.
    ///
    /// <para>One class needs this and the design says why. A beneficiary photograph is administrative by
    /// visibility — it is not a clinical record — but for a refugee population it is identity-sensitive,
    /// biometric-adjacent data (design 39 §5), and the administrative allow-list is far too wide for it: it
    /// includes finance, claims and platform admins, none of whom has an identification need. So the photo
    /// carries its own list, and the visibility class alone is not enough to answer the question.</para>
    /// </summary>
    public static bool MayDownload(
        DocumentClass documentClass, NoteVisibility visibility, IReadOnlyCollection<string> roles,
        bool hasSensitiveGrant = false)
    {
        ArgumentNullException.ThrowIfNull(roles);
        // The photo's list is the NARROWER of the two, never the wider: it is an additional gate on top of the
        // class rules, not an exemption from them.
        if (documentClass == DocumentClass.IdentityPhoto)
            return Mersal.Authz.ProfilePhotoAccess.MayView(roles)
                   && MayDownload(visibility, roles, hasSensitiveGrant);

        return MayDownload(visibility, roles, hasSensitiveGrant);
    }

    /// <summary>
    /// Whether this caller may UPLOAD a document of this class.
    ///
    /// <para>Separate from download on purpose: a finance user may attach an invoice to a member but must not
    /// be able to file a past medical history — a clinical record entering the system unsigned by anyone
    /// clinical is both a data-quality problem and a way to smuggle clinical content in under an
    /// administrative badge.</para>
    /// </summary>
    public static bool MayUpload(DocumentClass documentClass, IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        // A photograph is CAPTURED at the desk, with the person present and consenting — so the roles that may
        // file one are the roles that meet them: registration and the front desk. Not clinicians (a photo is
        // not a clinical act), not finance, not admins.
        if (documentClass == DocumentClass.IdentityPhoto)
        {
            string[] photographers =
                ["reception", "beneficiary_mgmt", "beneficiary_mgmt_supervisor", "super_admin"];
            return roles.Any(r => photographers.Contains(r, StringComparer.Ordinal));
        }

        var floor = DocumentClassification.DefaultFor(documentClass);
        if (floor is NoteVisibility.Administrative or NoteVisibility.Financial)
            return roles.Any(r => Downloaders[floor].Contains(r, StringComparer.Ordinal));

        // Clinical material needs a clinical or beneficiary-management hand on it.
        string[] clinicalUploaders =
        [
            "doctor", "nurse", "medical_approval", "medical_director", "case_manager",
            "beneficiary_mgmt", "beneficiary_mgmt_supervisor", "super_admin",
        ];
        return roles.Any(r => clinicalUploaders.Contains(r, StringComparer.Ordinal));
    }
}
