using Mersal.Authz;

namespace Mersal.Profile.Domain;

// The 15 section payloads and their VARIANT PROJECTIONS (design 39 §4's parenthesised qualifiers: V(meta),
// V(admin), V(own orders), V(status), V(amounts), …).
//
// Every projection returns a NEW, narrower record. Nothing sets a field to "" or hides it behind a flag: a
// dropped field is null, the serializer omits nulls, and the reflection tests read the SERIALIZED JSON. That is
// the only way "the payload never contains a withheld field" (design 39 §7.1) can be asserted rather than
// asserted-about.

/// <summary>Non-colour status semantics (21-accessibility): hue is never the only signal.</summary>
public sealed record StatusCue(string Label, string Icon, string Shape, string Tone)
{
    public static StatusCue For(string status) => status switch
    {
        "Active" => new("Active", "check-circle", "circle", "positive"),
        "Suspended" => new("Suspended", "pause", "square", "caution"),
        "Expired" => new("Expired", "clock-x", "diamond", "caution"),
        "Blocked" => new("Blocked", "ban", "octagon", "critical"),
        "Inactive" => new("Inactive", "minus-circle", "square", "neutral"),
        _ => new("Pending", "hourglass", "triangle", "neutral"),
    };
}

// ---------------------------------------------------------------- 1. header

public sealed record ContactSummary(string? Phone, string? PreferredChannel);

/// <summary>Identity strip. <see cref="PhotoUrl"/> is stripped separately from the variant, by
/// <see cref="ProfilePhotoAccess"/> — the photo's allow-list is narrower than the header's (design 39 §5).</summary>
public sealed record HeaderSection(
    Guid BeneficiaryId,
    string? MemberNo,
    string DisplayName,
    string? DisplayNameAr,
    string? AgeBand,
    string? Sex,
    string Status,
    StatusCue StatusCue,
    string? BranchName,
    string? PreferredLanguage,
    ContactSummary? Contact,
    string? PhotoUrl,
    /// <summary>Cover relationship — Principal, Spouse, Child, Dependent. Identity, not clinical: which
    /// person on a policy this is.</summary>
    string? Relationship = null,
    /// <summary>ISO country code. The member card in beneficiary management has always shown it; the profile
    /// header simply never carried it.</summary>
    string? NationalityCode = null,
    /// <summary>The exact birth date, so the header can show an AGE rather than a band.
    ///
    /// <para>More disclosive than <c>AgeBand</c> and therefore stripped by <c>min</c>: labs and pharmacies get
    /// the band, which is all a specimen label or a dose check needs. The roles on the full header read the
    /// birth date one screen away in any case.</para></summary>
    DateOnly? BirthDate = null,
    /// <summary>Travels WITH the date, always. An estimated birth date rendered as an exact age is how an
    /// estimate quietly becomes a hard eligibility cutoff.</summary>
    bool BirthDateIsApproximate = false)
{
    /// <summary>
    /// <c>min</c> keeps only what identifies the person: names, member number, age band, sex and status.
    ///
    /// <para>Age band and sex stay because the roles that get <c>min</c> include labs and pharmacies, where they
    /// are a specimen-labelling and dosing safety check, not curiosity. Branch, preferred language and contact
    /// details go: none of them helps identify anyone, and a phone number is the field a min-necessary review
    /// asks about first.</para>
    /// </summary>
    public HeaderSection Project(string? variant) => variant switch
    {
        ProfileVariants.Min => this with
        {
            BranchName = null, PreferredLanguage = null, Contact = null, PhotoUrl = null,
            // The exact date goes; AgeBand stays. Relationship and nationality identify a person on a policy,
            // which is not what a specimen label or a dose check is for.
            Relationship = null, NationalityCode = null, BirthDate = null, BirthDateIsApproximate = false,
        },
        _ => this,
    };

    /// <summary>Drop the photo for a caller outside the design-39 §5 allow-list. Applied after the variant, so
    /// a clinician's full header keeps it and a finance header has no photo field at all.</summary>
    public HeaderSection WithoutPhoto() => this with { PhotoUrl = null };
}

// ---------------------------------------------------------------- 2. alerts

public sealed record AllergyAlert(string Allergen, string? Reaction, string Severity);
public sealed record FlagAlert(string Kind, string Label, string Tone);

public sealed record AlertsSection(
    IReadOnlyList<AllergyAlert> Allergies,
    IReadOnlyList<FlagAlert>? CriticalFlags,
    IReadOnlyList<FlagAlert>? InteractionWarnings,
    IReadOnlyList<FlagAlert>? OperationalFlags)
{
    /// <summary><c>allergy</c> — labs and pharmacies get the allergy list and nothing else: contrast reactions
    /// and drug-allergy checking are their job; a no-show flag or an eligibility warning is not.</summary>
    public AlertsSection Project(string? variant) => variant switch
    {
        ProfileVariants.Allergy => new(Allergies, null, null, null),
        _ => this,
    };
}

// ---------------------------------------------------------------- 3. coverage

public sealed record CoverageLimitLine(
    string Category, decimal? AnnualLimit, decimal? Consumed, decimal? Remaining,
    decimal? CostSharePercent, string? CostShareTier);

public sealed record CoverageSection(
    string? PayerName,
    string? PolicyNo,
    string? PlanLabel,
    int? PlanVersion,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    string? WaitingPeriodState,
    IReadOnlyList<CoverageLimitLine> Categories)
{
    public const string PharmacyCategory = "Pharmacy";

    public CoverageSection Project(string? variant) => variant switch
    {
        // Finance administers money, not benefit design: the amounts and the plan they price against, without
        // the eligibility mechanics (effective window, waiting period) that decide whether care may proceed.
        ProfileVariants.Amounts => this with { EffectiveFrom = null, EffectiveTo = null, WaitingPeriodState = null },
        // A pharmacy needs one number: what is left in the pharmacy pot, so it can tell a member why a dispense
        // is short. It has no business knowing the annual dental limit.
        ProfileVariants.PharmacyLimit => new(
            null, null, PlanLabel, null, null, null, null,
            [.. Categories.Where(c => string.Equals(c.Category, PharmacyCategory, StringComparison.OrdinalIgnoreCase))]),
        _ => this,
    };
}

// ---------------------------------------------------------------- 4. past medical history

public sealed record CodedCondition(string System, string Code, string Display, string? ClinicalStatus, DateOnly? OnsetOn);
public sealed record HistoricalRecord(Guid LinkId, string DocumentClass, string Title, DateOnly? DocumentDate);

public sealed record PastMedicalHistorySection(
    IReadOnlyList<CodedCondition> Conditions,
    string? Narrative,
    IReadOnlyList<HistoricalRecord>? UploadedRecords)
{
    /// <summary><c>summary</c> — coded conditions only. A case manager coordinating transport and appointments
    /// needs to know a person is diabetic; the referring clinician's narrative is not part of that job.</summary>
    public PastMedicalHistorySection Project(string? variant) => variant switch
    {
        ProfileVariants.Summary => new(Conditions, null, null),
        _ => this,
    };
}

// ---------------------------------------------------------------- 5. encounters

public sealed record EncounterRow(
    string EncounterRef, DateTimeOffset OccurredAt, string? BranchName,
    string? ClinicianName, string? Specialty, string? Reason, string Status,
    /// <summary>The handle a clinical row is opened by. Null under <c>meta</c> — see the projection.</summary>
    string? EncounterId = null);

public sealed record EncountersSection(IReadOnlyList<EncounterRow> Items)
{
    /// <summary><c>meta</c> — the visit happened, when, where, with whom. <b>The reason for the visit is
    /// dropped</b>: "chest pain" is a clinical fact, and reception, finance and beneficiary management get the
    /// logistics of a visit, never its content.</summary>
    public EncountersSection Project(string? variant) => variant switch
    {
        // `meta` drops the ID as well as the reason. It is not clinical content, it is a CAPABILITY handle:
        // the roles on this variant (reception, finance, beneficiary management) have no encounter workspace
        // to open, so sending them a way to address one is a field with no use and a future misuse.
        ProfileVariants.Meta => new([.. Items.Select(i => i with { Reason = null, EncounterId = null })]),
        _ => this,
    };
}

// ---------------------------------------------------------------- 6. investigations

/// <summary>An ordered investigation. <see cref="Restricted"/> + <see cref="ResultSummary"/> carry the design-37
/// §6 outcome the OWNING service computed: a restricted result arrives here with existence metadata and no
/// value, and the profile has nothing to strip because it was never sent one.</summary>
public sealed record InvestigationRow(
    string OrderRef, Guid LineId, string Category, DateTimeOffset OrderedOn, string Status,
    string? ProviderName, string? ResultSummary, bool Restricted, string? SensitivityLevel);

public sealed record InvestigationsSection(IReadOnlyList<InvestigationRow> Items)
{
    /// <summary><c>own-orders</c> is a ROW filter applied by the owning service under provider-ownership — the
    /// profile asserts it here as defence in depth rather than performing it, because a filter the aggregator
    /// applies is a filter the owning service has stopped applying.</summary>
    public InvestigationsSection Project(string? variant) => this;

    /// <summary>Defence in depth for the sensitive gate: strip any value that reached us marked restricted.
    /// If this ever changes anything, the owning service has a bug — and the test says so.</summary>
    public InvestigationsSection WithSensitiveValuesRemoved() =>
        new([.. Items.Select(i => i.Restricted ? i with { ResultSummary = null } : i)]);
}

// ---------------------------------------------------------------- 7. prescriptions

public sealed record RxRow(
    string RxRef, string DrugDisplay, string Status, DateTimeOffset PrescribedOn,
    DateTimeOffset? DispensedOn, string? BatchNo, DateOnly? ExpiryDate, string? SubstitutedWith);

public sealed record PrescriptionsSection(IReadOnlyList<RxRow> Items)
{
    /// <summary><c>own-rx</c> is a row filter the owning service applies under provider-ownership.</summary>
    public PrescriptionsSection Project(string? variant) => this;
}

// ---------------------------------------------------------------- 8. authorizations

public sealed record AuthorizationRow(
    string AuthNo, string? ServiceCategory, string Status, DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt, DateOnly? ValidUntil, string? Rationale, decimal? ApprovedAmount);

public sealed record AuthorizationsSection(IReadOnlyList<AuthorizationRow> Items)
{
    public AuthorizationsSection Project(string? variant) => variant switch
    {
        // Reception tells a member "your MRI is approved until the 30th". The clinical RATIONALE for that
        // decision, and the money, are two different zones and neither is theirs.
        ProfileVariants.Status => new([.. Items.Select(i => i with { Rationale = null, ApprovedAmount = null })]),
        // Finance prices the decision; it still never reads the clinical reasoning behind it.
        ProfileVariants.Cost => new([.. Items.Select(i => i with { Rationale = null })]),
        _ => this,
    };
}

// ---------------------------------------------------------------- 9. referrals

public sealed record ReferralRow(
    string ReferralRef, string Status, string? RequestedSpecialty,
    DateTimeOffset CreatedAt, DateTimeOffset? LoopClosedAt);

public sealed record ReferralsSection(IReadOnlyList<ReferralRow> Items)
{
    public ReferralsSection Project(string? variant) => this;
}

// ---------------------------------------------------------------- 10. documents

public sealed record DocumentRow(
    Guid LinkId, string DocumentClass, string VisibilityClass, string Title,
    DateOnly? DocumentDate, DateTimeOffset UploadedAt, string Status, bool MayDownload);

public sealed record DocumentsSection(IReadOnlyList<DocumentRow> Items)
{
    public const string Administrative = "Administrative";
    public const string Financial = "Financial";

    /// <summary><c>admin</c> — administrative paperwork only. A scanned discharge summary filed as Clinical
    /// never appears in an administrative caller's list, which is the point of 19.3b's classification floor.</summary>
    public DocumentsSection Project(string? variant) => variant switch
    {
        ProfileVariants.Admin => new([.. Items.Where(i =>
            string.Equals(i.VisibilityClass, Administrative, StringComparison.Ordinal))]),
        _ => this,
    };
}

// ---------------------------------------------------------------- 11. notes

/// <summary>A policy/member note. <see cref="Body"/> is null and <see cref="Withheld"/> true when the caller
/// lacks the note's visibility class — the note's EXISTENCE is not the secret, its content is (19.3).</summary>
public sealed record NoteRow(
    Guid NoteId, string NoteType, string VisibilityClass, string? Body,
    string AuthorDisplay, DateTimeOffset CreatedAt, bool Withheld, bool Pinned);

public sealed record NotesSection(IReadOnlyList<NoteRow> Items)
{
    public NotesSection Project(string? variant) => variant switch
    {
        ProfileVariants.Admin => Only(DocumentsSection.Administrative),
        ProfileVariants.Financial => Only(DocumentsSection.Administrative, DocumentsSection.Financial),
        _ => this,
    };

    private NotesSection Only(params string[] classes) =>
        new([.. Items.Where(i => classes.Contains(i.VisibilityClass, StringComparer.Ordinal))]);
}

// ---------------------------------------------------------------- 12. financial

public sealed record FinancialClaimRow(
    string ClaimNo, DateOnly ServiceDate, decimal BilledAmount, decimal? ApprovedAmount,
    decimal? MemberShare, string Status);

/// <summary>Money only. There is no property on this type that can hold a diagnosis, a result or a note — the
/// oldest hard rule in 11-permission-matrix, expressed as a shape rather than as a filter.</summary>
public sealed record FinancialSection(
    string Currency,
    decimal CostShareOwed,
    string? SettlementStatus,
    IReadOnlyList<FinancialClaimRow>? Claims)
{
    /// <summary><c>summary</c> — the Medical Director sees what care costs in aggregate, not the claim ledger.</summary>
    public FinancialSection Project(string? variant) => variant switch
    {
        ProfileVariants.Summary => this with { Claims = null },
        _ => this,
    };
}

// ---------------------------------------------------------------- 13. case management

public sealed record CaseRow(Guid CaseId, string CaseNo, string Status, string? Category, DateTimeOffset OpenedAt);
public sealed record CoordinationTaskRow(Guid TaskId, string Title, string Status, DateOnly? DueOn);
public sealed record EscalationRow(Guid EscalationId, string Reason, string Status, DateTimeOffset RaisedAt);

public sealed record CaseManagementSection(
    IReadOnlyList<CaseRow> Cases,
    IReadOnlyList<CoordinationTaskRow> Tasks,
    IReadOnlyList<EscalationRow> Escalations)
{
    public CaseManagementSection Project(string? variant) => this;
}

// ---------------------------------------------------------------- 14. timeline

public sealed record TimelineRow(
    DateTimeOffset At, string EventType, string VisibilityClass,
    string? ActorDisplay, string? Summary, string SourceService);

public sealed record TimelineSection(IReadOnlyList<TimelineRow> Items)
{
    public const string Access = "Access";

    public TimelineSection Project(string? variant) => variant switch
    {
        ProfileVariants.Admin => Only(DocumentsSection.Administrative),
        ProfileVariants.Financial => Only(DocumentsSection.Administrative, DocumentsSection.Financial),
        // An org/super admin administers ACCESS. "Who looked at this patient" is theirs; what was found is not.
        ProfileVariants.Access => Only(Access),
        _ => this,
    };

    private TimelineSection Only(params string[] classes) =>
        new([.. Items.Where(i => classes.Contains(i.VisibilityClass, StringComparer.Ordinal))]);
}

// ---------------------------------------------------------------- 15. call history

public sealed record LinkedArtifact(string Type, string Ref, string? Action);

public sealed record CallVerificationDetail(string Result, IReadOnlyList<string> IdentifierTypes);

/// <summary>
/// One contact-centre interaction, ALREADY PROJECTED by callcentre-service to the level the caller's role
/// resolves to (design 39 §5b). The profile does not re-project it: a second projection over the same rows is a
/// second answer to the same question, and the two would drift.
///
/// <para><see cref="CopyText"/> is generated by callcentre-service from THIS object in the same code path, so a
/// field the projection dropped cannot appear in the clipboard block.</para>
/// </summary>
public sealed record CallHistoryRow(
    string CallRef,
    string Direction,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    string? BranchCode,
    string? AgentDisplayName,
    string? ReasonCode,
    string? Outcome,
    CallVerificationDetail? Verification,
    string? Summary,
    bool SummaryEdited,
    IReadOnlyList<LinkedArtifact>? LinkedArtifacts,
    string CopyText);

public sealed record CallHistorySection(
    string Level,
    IReadOnlyList<CallHistoryRow> Items,
    string? NextCursor)
{
    /// <summary>No-op: the level was decided and applied by callcentre-service. Present so every section answers
    /// the same interface and the composer has no special case.</summary>
    public CallHistorySection Project(string? variant) => this;
}
