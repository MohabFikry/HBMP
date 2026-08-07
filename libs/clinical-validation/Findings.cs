namespace Mersal.ClinicalValidation;

/// <summary>
/// The outcome of one check on one prescription line. <b>Five</b> states, never four.
/// </summary>
/// <remarks>
/// The distinction that matters is between the three states that are an <i>answer</i> and the two that are
/// not. <see cref="NotChecked"/> and <see cref="Unavailable"/> are both "we do not know", and neither may
/// ever be presented as <see cref="Ok"/> (doc 43 invariant 2). The UI renders them as a separate visual
/// class — dashed border, hollow icon — so that reads before colour or text does.
/// </remarks>
public enum CheckState
{
    /// <summary>The check ran against real data and found nothing to report.</summary>
    Ok,

    /// <summary>Something to tell the prescriber. Overridable with a recorded reason; never blocking.</summary>
    Warning,

    /// <summary>
    /// Refused. Reachable ONLY from a benefit rule — see <see cref="BenefitOutcome"/>. A clinical check that
    /// blocked would be blocking care on data of uncertain provenance, which doc 43 §1 D1 rules out.
    /// </summary>
    Blocked,

    /// <summary>The check could not run because the data does not exist — no rule, no record, no diagnosis.</summary>
    NotChecked,

    /// <summary>The data source failed. Distinct from <see cref="NotChecked"/>: this one is expected to work.</summary>
    Unavailable,
}

/// <summary>The five checks a prescription line goes through (doc 43 §6, phase 26.3).</summary>
public enum CheckKind
{
    /// <summary>Does a recorded diagnosis appear in this drug's listed indications?</summary>
    Indication,

    /// <summary>Drug–drug interactions, across lines and against active medications.</summary>
    Interaction,

    /// <summary>The drug against the beneficiary's recorded allergies.</summary>
    Allergy,

    /// <summary>Dose and duration, against structured rules where one exists for the drug.</summary>
    DoseDuration,

    /// <summary>Formulary, exclusion, limit and pre-auth. The only check that may block.</summary>
    Benefit,

    /// <summary>
    /// Two lines that duplicate each other — the same molecule, or the same therapeutic class.
    /// </summary>
    /// <remarks>
    /// Phase 26 skipped it outright: <c>if (a.DrugId == b.DrugId) continue;</c> in the interaction pass
    /// dropped the same product twice, and nothing looked at two DIFFERENT products sharing an ingredient.
    /// Two trade names holding the same molecule is the commonest real prescribing duplication, and the
    /// paracetamol case — where the second dose hides inside a cold-and-flu compound — is the classic
    /// accidental overdose. Once products decompose to molecules (28.1), detecting it is nearly free.
    /// </remarks>
    Duplication,

    /// <summary>
    /// Is this medicine DANGEROUS IN a condition the patient has?
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Indication"/>, and design 44 §5 is emphatic about why. Asking
    /// "is this drug USED FOR this condition" produces a mismatch on every off-label prescription — which is
    /// legitimate and common, so the warning fires constantly and is dismissed constantly. Asking "is this
    /// drug DANGEROUS IN this condition" produces a finding only when there is harm to avoid. Conflating the
    /// two is what makes the first one noise and buries the second inside it.
    /// </remarks>
    Contraindication,
}

/// <summary>
/// The states a <b>clinical</b> check may return. There is deliberately no <c>Blocked</c>.
/// </summary>
/// <remarks>
/// Doc 43 invariant 1 — "benefit rules may block; clinical checks may only warn" — is enforced here rather
/// than by review. Every clinical checker is typed to return this enum, so a clinical check cannot express a
/// refusal at all: writing one is a compile error, not something a reader has to notice. Blocking care on
/// automated advice of uncertain provenance would be the greater harm (doc 43 §1 D1).
/// </remarks>
public enum ClinicalState
{
    Ok,
    Warning,
    NotChecked,
    Unavailable,
}

/// <summary>The states a <b>benefit</b> rule may return. This is the only path to a refusal.</summary>
/// <remarks>
/// A benefit rule may block because "not covered" is a factual, deterministic statement about a policy that
/// the platform itself owns and can explain line by line — unlike a clinical advisory.
/// </remarks>
public enum BenefitState
{
    Allowed,
    Warning,
    Blocked,
    NotChecked,
    Unavailable,
}

/// <summary>
/// How serious a clinical finding is. Carried by <b>every</b> check kind, not only interactions.
/// </summary>
/// <remarks>
/// <para>
/// Phase 26 called this <c>InteractionSeverity</c> and only the interaction check produced one; every
/// finding, of every kind, was a <see cref="CheckState.Warning"/> requiring a typed acknowledgement. A
/// contraindicated pair and a trivial one demanded the same click.
/// </para>
/// <para>
/// That is the single best-documented failure mode in clinical decision support. Override rates above 90%
/// are routinely reported in the literature and the mechanism is always this one: when everything
/// interrupts, clinicians learn to dismiss everything, and the alerts that mattered are dismissed with the
/// rest. Uniform alerting does not merely annoy — it destroys the signal of the alerts worth stopping for.
/// </para>
/// <para>
/// So severity became a property of the finding rather than of one check, and
/// <see cref="Finding.RequiresAcknowledgement"/> reads it. <b>Only Contraindicated and Major gate
/// submission</b>; Moderate renders inline and Minor collapses. This changes INTERRUPTION, never blocking:
/// <see cref="ClinicalState"/> still has no <c>Blocked</c>.
/// </para>
/// </remarks>
public enum ClinicalSeverity
{
    /// <summary>Reference only. Collapsed by default.</summary>
    Minor,

    /// <summary>Awareness, not intervention. Renders inline; does NOT gate submission.</summary>
    Moderate,

    /// <summary>Clinically significant, action usually needed. Interrupts and gates submission.</summary>
    Major,

    /// <summary>"Do not co-prescribe." Interrupts, gates, and requires a typed override reason.</summary>
    Contraindicated,
}

/// <summary>
/// One check's verdict on one line, with the provenance and messages needed to display and store it.
/// </summary>
/// <remarks>
/// Constructed only through <see cref="Clinical"/> and <see cref="Benefit"/>. The constructor is private so
/// that <see cref="CheckState.Blocked"/> cannot be reached except through the benefit factory — the type
/// system carries invariant 1 rather than a comment asking people to honour it.
/// </remarks>
public sealed record Finding
{
    private Finding() { }

    public Guid LineId { get; private init; }
    public Guid? DrugId { get; private init; }
    public CheckKind Kind { get; private init; }
    public CheckState State { get; private init; }

    /// <summary>English message. A finding with no explanation is not actionable.</summary>
    public string MessageEn { get; private init; } = default!;

    /// <summary>Arabic message. The platform is bilingual; a finding shown only in English is not shown.</summary>
    public string MessageAr { get; private init; } = default!;

    /// <summary>Null only when the state is <see cref="CheckState.Unavailable"/> — there was no source to attribute.</summary>
    public ProvenanceInfo? Provenance { get; private init; }

    public ClinicalSeverity? Severity { get; private init; }

    /// <summary>For a cross-line interaction, the other line involved.</summary>
    public Guid? RelatedLineId { get; private init; }

    /// <summary>
    /// Verbatim source text supporting the finding — the manufacturer's own words.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MessageEn"/> because it is quoted, not authored: it is the sentence in a
    /// drug label that named another drug, or the label's dosing section shown for the prescriber to read
    /// against what they typed. It is <b>English only</b>, because that is the language the label is
    /// published in, and inventing an Arabic translation of a regulatory document would be a worse answer
    /// than saying which language it is in. The message carrying it says so in both languages.
    /// </remarks>
    public string? ReferenceText { get; private init; }

    /// <summary>A clinical finding. Cannot block — <see cref="ClinicalState"/> has no such value.</summary>
    public static Finding Clinical(
        Guid lineId, Guid? drugId, CheckKind kind, ClinicalState state,
        string messageEn, string messageAr, ProvenanceInfo? provenance,
        ClinicalSeverity? severity = null, Guid? relatedLineId = null, string? referenceText = null)
    {
        if (kind is CheckKind.Benefit)
        {
            throw new ArgumentException("Benefit findings are created through Finding.Benefit.", nameof(kind));
        }

        return new Finding
        {
            LineId = lineId, DrugId = drugId, Kind = kind,
            State = state switch
            {
                ClinicalState.Ok => CheckState.Ok,
                ClinicalState.Warning => CheckState.Warning,
                ClinicalState.NotChecked => CheckState.NotChecked,
                ClinicalState.Unavailable => CheckState.Unavailable,
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            },
            MessageEn = messageEn, MessageAr = messageAr, Provenance = provenance,
            Severity = severity, RelatedLineId = relatedLineId, ReferenceText = referenceText,
        };
    }

    /// <summary>A benefit finding — the only kind that may refuse.</summary>
    public static Finding Benefit(
        Guid lineId, Guid? drugId, BenefitState state,
        string messageEn, string messageAr, ProvenanceInfo? provenance) =>
        new()
        {
            LineId = lineId, DrugId = drugId, Kind = CheckKind.Benefit,
            State = state switch
            {
                BenefitState.Allowed => CheckState.Ok,
                BenefitState.Warning => CheckState.Warning,
                BenefitState.Blocked => CheckState.Blocked,
                BenefitState.NotChecked => CheckState.NotChecked,
                BenefitState.Unavailable => CheckState.Unavailable,
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            },
            MessageEn = messageEn, MessageAr = messageAr, Provenance = provenance,
        };

    /// <summary>
    /// Whether the prescriber must acknowledge this with a reason before the line may be submitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Severity-aware since phase 28 (design 44 §2). It used to be "any Warning", which put a
    /// contraindicated combination and a minor one behind the same mandatory click and taught prescribers
    /// to type a reason reflexively — the documented route to 90%+ override rates.
    /// </para>
    /// <para>
    /// <b>A null severity still interrupts.</b> Manufacturer-label interactions carry no grade, because a
    /// label states an effect rather than a rank. Treating "ungraded" as "not serious" would be this code
    /// inventing a clinical judgement it has no source for, and it would silence the only interaction
    /// source the platform had before the curated list was populated.
    /// </para>
    /// </remarks>
    public bool RequiresAcknowledgement =>
        State is CheckState.Warning
        && Severity is null or ClinicalSeverity.Major or ClinicalSeverity.Contraindicated;

    /// <summary>
    /// Whether the override reason must be TYPED rather than merely clicked through.
    /// </summary>
    /// <remarks>
    /// Contraindicated only. These are "do not co-prescribe" — rare, and the one place where the friction of
    /// making somebody write a sentence is proportionate. The reason is recorded against the prescription
    /// and surfaced to the approver, so proceeding is a decision with a name on it.
    /// </remarks>
    public bool RequiresTypedReason =>
        RequiresAcknowledgement && Severity is ClinicalSeverity.Contraindicated;

    /// <summary>
    /// Whether this finding should interrupt the prescriber rather than render beside the line.
    /// </summary>
    /// <remarks>
    /// The same set that gates submission, plus the two states that are not answers. An
    /// <see cref="CheckState.Unavailable"/> is not a severity question — the prescriber has to know a check
    /// did not run, whatever it would have said.
    /// </remarks>
    public bool IsInterruptive => RequiresAcknowledgement || State is CheckState.Blocked;

    /// <summary>Whether this finding prevents submission outright. Only a benefit rule can produce one.</summary>
    public bool IsBlocking => State is CheckState.Blocked;
}

/// <summary>The full result of a validation run across every line.</summary>
public sealed record ValidationResult(IReadOnlyList<Finding> Findings, DateTimeOffset RanAt)
{
    /// <summary>
    /// The state to show against a line: the worst of its findings.
    /// </summary>
    /// <remarks>
    /// Precedence is <c>Blocked > Unavailable > Warning > NotChecked > Ok</c>. Unavailable deliberately
    /// outranks Warning: a line with one warning and one failed check is not adequately described by
    /// "Warning", because the prescriber would have no way to see that something went unchecked.
    /// </remarks>
    public CheckState StateFor(Guid lineId)
    {
        var states = Findings.Where(f => f.LineId == lineId).Select(f => f.State).ToList();
        if (states.Count == 0) return CheckState.NotChecked;

        foreach (var rank in new[]
                 { CheckState.Blocked, CheckState.Unavailable, CheckState.Warning, CheckState.NotChecked })
        {
            if (states.Contains(rank)) return rank;
        }
        return CheckState.Ok;
    }

    /// <summary>Findings on a line that must be acknowledged with a reason before it can be submitted.</summary>
    public IReadOnlyList<Finding> UnacknowledgedBlockers(Guid lineId)
        => [.. Findings.Where(f => f.LineId == lineId && f.RequiresAcknowledgement)];

    /// <summary>True when any line carries a benefit refusal. Such a line cannot be submitted at all.</summary>
    public bool HasBlocking => Findings.Any(f => f.IsBlocking);
}
