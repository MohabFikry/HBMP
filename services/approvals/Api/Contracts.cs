using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Mersal.Authz;

namespace Mersal.Approvals.Api;

// ---- Ingestion (the routing-saga / event-consumer seam) ----

/// <summary>Create a Submitted authorization from a routed order-line / prescription (or a manual seed). This is
/// the seam the phase-4 routing saga / the OrderPendingApproval|RxSubmitted event consumer targets.</summary>
public sealed record CreateAuthorizationRequest(
    Guid BeneficiaryId,
    AuthSource Source,
    string? SourceRef,
    Guid? RequestingProviderId,
    IReadOnlyList<string> ServiceCodes,
    string? RequestedScope,
    AuthPriority Priority,
    /// <summary>
    /// The user id of the clinician who ordered the thing being authorized — the person waiting for the answer.
    ///
    /// <para>Audit §11.3 named this as the one real limit on the auth notifications. This endpoint requires
    /// <c>auth:ingest</c>, a machine-only scope, so <c>CreatedBy</c> is the routing saga rather than a human.
    /// <c>NotifyDecisionAsync</c> addresses the decision notice to <c>CreatedBy</c> and returns early when it
    /// is blank — so on the ordinary flow (order or prescription routed to approval) the decision was
    /// correctly not sent to anybody, and the clinician who asked for it learned the answer by going and
    /// looking. Only the break-glass path, where a human IS the caller, ever notified anyone.</para>
    ///
    /// <para>So the ingesting service passes the clinician forward, exactly as <c>registration.created_by</c>
    /// carries the filing officer. Optional: a genuinely unattended authorization (a migration, a seed) has no
    /// human behind it, and inventing one would address a notice to a machine account. When it is absent the
    /// notice is still correctly not sent.</para>
    /// </summary>
    string? OrderedByUserId = null,
    /// <summary>
    /// The encounter the thing being authorized was ordered in (ADR-0031).
    ///
    /// <para>Same seam and same argument as <see cref="OrderedByUserId"/>, for a different question. That one
    /// carries WHO is waiting so the decision notice has an addressee; this carries WHICH VISIT so the
    /// decision lands on the patient's episode. An authorization is one of the few things that can hold a
    /// consultation open for days, and until it carried the encounter the appointment's timeline could show
    /// "sent for approval" and then nothing — the desk could see the wait start and never see it end.</para>
    ///
    /// <para>Optional, because a manual authorization is raised by a reviewer with no visit in hand and a
    /// migration has none at all. Absent means the decision is simply not stepped onto any episode; a guessed
    /// encounter would put one member's authorization on another member's timeline.</para>
    /// </summary>
    Guid? EncounterId = null);

// ---- Worklist projection (MIN-NECESSARY — no clinical payload) ----

/// <summary>The reviewer-inbox row. Deliberately carries NO clinical fields (11-permission-matrix §3.2): key,
/// beneficiary display-min (id only), requested service codes, priority, status, SLA due, and elapsed TAT.</summary>
public sealed record WorklistItemView(
    Guid AuthorizationId,
    string AuthNo,
    Guid BeneficiaryId,
    IReadOnlyList<string> ServiceCodes,
    string Priority,
    string Status,
    DateTimeOffset? SlaDueAt,
    bool SlaBreached,
    long TatElapsedSeconds,
    /// <summary>
    /// Where the request came from — OrderLine / Prescription / Manual / ValidityExtension.
    ///
    /// <para>Without it a validity-extension request is indistinguishable from a benefit authorization in
    /// the queue, and a reviewer opens the clinical review view looking for a diagnosis and a service code
    /// that a request to re-date a prescription does not have. It is not clinical data; it is what KIND of
    /// question this is, which is the first thing anyone triaging a queue needs.</para>
    /// </summary>
    string Source,
    /// <summary>
    /// The originating item's own reference — RX-2026-000312 / ORD-2026-000900.
    /// </summary>
    /// <remarks>Populated for a validity-extension request (the expired item) and for a fulfilment
    /// authorization (what was delivered against). Null elsewhere. It is the only string on this row a human
    /// can look up, and both of those rows are meaningless without it.</remarks>
    string? ItemReference,
    /// <summary>
    /// The requester's stated reason, on validity-extension rows only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The worklist is otherwise a NO-clinical-payload projection, and this is a deliberate, bounded
    /// exception. The reason on an extension request is logistics authored by a pharmacist or technician —
    /// "the patient could not travel before it lapsed" — and it is the ENTIRE substance of the decision.
    /// Leaving it out would force a reviewer through the PHI-audited clinical review view to read one
    /// sentence that is not clinical, adding an audited access to the patient's record for no clinical
    /// question. That is a worse disclosure outcome, not a better one.
    /// </para>
    /// <para>Null for every other source, so the exception cannot widen by accident.</para>
    /// </remarks>
    string? ExtensionReason,
    /// <summary>
    /// <c>Review</c> — a question waiting for an answer — or <c>Fulfilment</c>, a record of something already
    /// handed over at a counter (ADR-0034). The two live in one aggregate and one number space; a reviewer
    /// triaging a row needs to know which of them they are looking at before anything else on it means
    /// something.
    /// </summary>
    string Kind = "Review",
    /// <summary>Who is holding this, or null if nobody has picked it up.</summary>
    /// <remarks>The projection carried no ownership at all, so a queue worked by several reviewers could not
    /// answer "is this mine" or "has anyone taken it" — and the client compensated by showing every row to
    /// everyone. It is a staff id, not patient data.</remarks>
    Guid? AssignedReviewerId = null,
    /// <summary>The provider that asked, or null on a manual authorization — which by definition has none.</summary>
    /// <remarks>The client rendered the literal string "Provider" as the requester on every row, including the
    /// manual ones where it was flatly untrue. Carrying the real field lets the screen say what it knows and
    /// say nothing where it knows nothing.</remarks>
    Guid? RequestingProviderId = null,
    /// <summary>When the request was actually submitted.</summary>
    /// <remarks>The client derived this as <c>now - tatElapsedSeconds</c>, recomputed on every render, so a
    /// row's submission time drifted forward as the page sat open. The server holds the real timestamp.</remarks>
    DateTimeOffset? SubmittedAt = null)
{
    public static WorklistItemView From(Authorization a, DateTimeOffset now)
    {
        // A fulfilment carries the same `itemRef` key, so one reader serves both. It carries no reason: a
        // dispense answers no question, and the per-item substitution reasons live on /items.
        var (itemRef, reason) = a.Source switch
        {
            AuthSource.ValidityExtension => ExtensionDetails(a.RequestedScope),
            _ when a.Kind == AuthKind.Fulfilment => (ExtensionDetails(a.RequestedScope).ItemRef, null),
            _ => (null, null),
        };

        return new(
            a.AuthorizationId, a.AuthNo, a.BeneficiaryId,
            Codes.Parse(a.ServiceCodes), a.Priority.ToString(), a.Status.ToString(),
            a.SlaDueAt, a.SlaBreached,
            (long)((a.DecidedAt ?? now) - a.SubmittedAt).TotalSeconds,
            a.Source.ToString(), itemRef, reason, a.Kind.ToString(),
            a.AssignedReviewerId, a.RequestingProviderId, a.SubmittedAt);
    }

    /// <summary>Reads the reference and reason back out of the request's stored scope. A malformed scope
    /// yields nulls rather than throwing — a queue that 500s because one row's json is odd is worse than a
    /// row that shows less than it could.</summary>
    private static (string? ItemRef, string? Reason) ExtensionDetails(string scopeJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(scopeJson);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("itemRef", out var r) ? r.GetString() : null,
                root.TryGetProperty("reason", out var w) ? w.GetString() : null);
        }
        catch (System.Text.Json.JsonException) { return (null, null); }
    }
}

/// <summary>
/// One delivered thing on a fulfilment authorization, as the approval team sees it (ADR-0034 Decision 3).
/// </summary>
/// <remarks>
/// <para>Codes, labels, a quantity, and — only when what was handed over differs from what was written — the
/// substituting pharmacist's reason. No diagnosis, no note, no indication: this row answers "what was
/// delivered against RX-2026-000410", which is a benefit question, not a clinical one.</para>
/// <para>The reason is the same bounded exception the worklist already makes for a validity-extension
/// request. It is logistics authored by a pharmacist — "prescribed brand out of stock this morning" — and it
/// is the entire substance of what a reviewer is looking at. Routing them through the PHI-audited clinical
/// review view to read one sentence would add an audited access to a patient's record for a question that is
/// not about the patient.</para>
/// </remarks>
public sealed record AuthorizationItemView(
    Guid ItemId,
    Guid? SourceLineId,
    string OrderedCode,
    string? OrderedLabel,
    string FulfilledCode,
    string? FulfilledLabel,
    decimal Quantity,
    /// <summary>True when what was handed over is not what was written. Derived from the two codes rather
    /// than stored, so it cannot disagree with them.</summary>
    bool Substituted,
    string? SubstitutionReason,
    DateTimeOffset FulfilledAt)
{
    public static AuthorizationItemView From(AuthorizationItem i) => new(
        i.ItemId, i.SourceLineId, i.OrderedCode, i.OrderedLabel, i.FulfilledCode, i.FulfilledLabel,
        i.Quantity, i.Substituted, i.SubstitutionReason, i.FulfilledAt);
}

/// <summary>Assign / state-change acknowledgement (no clinical fields).</summary>
public sealed record AuthorizationStateView(
    Guid AuthorizationId, string AuthNo, string Status, Guid? AssignedReviewerId, DateTimeOffset? SlaDueAt)
{
    public static AuthorizationStateView From(Authorization a) =>
        new(a.AuthorizationId, a.AuthNo, a.Status.ToString(), a.AssignedReviewerId, a.SlaDueAt);
}

// ---- Review view (the ONLY clinical-context DTO — field-scoped, PHI-audited) ----

/// <summary>The review view: the request header plus the field-scoped clinical projection. Assembled by the
/// <see cref="IClinicalContextProvider"/> under purpose PUR; every open writes a PHI-read audit event.</summary>
public sealed record ReviewView(
    Guid AuthorizationId,
    string AuthNo,
    Guid BeneficiaryId,
    string Source,
    string? SourceRef,
    IReadOnlyList<string> ServiceCodes,
    string RequestedScope,
    string Priority,
    string Status,
    bool ClinicalContextAvailable,
    string EmrSummary,
    IReadOnlyList<ReviewNote> Notes,
    IReadOnlyList<ReviewDocument> Documents)
{
    public static ReviewView From(Authorization a, ClinicalContext? ctx) => new(
        a.AuthorizationId, a.AuthNo, a.BeneficiaryId, a.Source.ToString(), a.SourceRef,
        Codes.Parse(a.ServiceCodes), a.RequestedScope, a.Priority.ToString(), a.Status.ToString(),
        ctx is not null,
        ctx?.EmrSummary ?? "clinical context unavailable",
        (ctx?.Notes ?? []).Select(ReviewNote.Of).ToList(),
        (ctx?.Documents ?? []).Select(ReviewDocument.Of).ToList());
}

/// <summary>A note as the reviewer sees it. H4/design 37 §6: a non-Standard note the caller may not access is
/// reduced to existence metadata only — its <see cref="Summary"/> is dropped and <see cref="Restricted"/> is set.</summary>
public sealed record ReviewNote(string Type, string Author, DateTimeOffset AuthoredAt, string Summary, bool Restricted)
{
    public static ReviewNote Of(ClinicalNote n) =>
        SensitiveDisclosure.IsRestricted(n.SensitivityLevel, n.CallerHasAccess)
            ? new(n.Type, n.Author, n.AuthoredAt, "[RESTRICTED — sensitive result; request access]", true)
            : new(n.Type, n.Author, n.AuthoredAt, n.Summary, false);
}

/// <summary>A supporting document/report as the reviewer sees it. A restricted item keeps its category (Kind) +
/// existence but drops the fetchable ref (DocumentId) and file name so it cannot be retrieved without a grant.</summary>
public sealed record ReviewDocument(Guid DocumentId, string Kind, string FileName, bool Restricted)
{
    public static ReviewDocument Of(SupportingDocument d) =>
        SensitiveDisclosure.IsRestricted(d.SensitivityLevel, d.CallerHasAccess)
            ? new(Guid.Empty, d.Kind, "", true)
            : new(d.DocumentId, d.Kind, d.FileName, false);
}

// ---- Decisions (phase 7.2) ----

public sealed record ApproveRequest(string? Rationale);
public sealed record PartialApproveRequest(IReadOnlyList<string> ApprovedScope, string Rationale);
public sealed record RejectRequest(string Rationale);
public sealed record RequestInfoRequest(string Rationale);

/// <summary>The recorded decision (append-only ledger row) returned to the reviewer, plus the resulting status.</summary>
public sealed record DecisionView(
    Guid AuthorizationId,
    string AuthNo,
    string Status,
    string Decision,
    string? Rationale,
    IReadOnlyList<string>? ApprovedScope,
    bool BreakGlass,
    int? TatSeconds,
    bool SlaBreached,
    DateTimeOffset DecidedAt)
{
    public static DecisionView From(Authorization a, AuthorizationDecision d) => new(
        a.AuthorizationId, a.AuthNo, a.Status.ToString(), d.Decision.ToString(), d.Rationale,
        d.ApprovedScope is null ? null : Codes.Parse(d.ApprovedScope), d.BreakGlass,
        a.TatSeconds, a.SlaBreached, d.DecidedAt);
}

// ---- Break-glass (phase 7.3) ----

public sealed record EmergencyApproveRequest(string Justification);
public sealed record OverrideRequest(string Justification);

/// <summary>Create-and-decide a manual authorization (no provider submission). The beneficiary is resolved by the
/// reviewer via the existing min-necessary member search; the decision must be Approved or PartiallyApproved.</summary>
public sealed record ManualAuthorizationRequest(
    Guid BeneficiaryId,
    IReadOnlyList<string> ServiceCodes,
    string? RequestedScope,
    AuthDecision Decision,
    IReadOnlyList<string>? ApprovedScope,
    string Justification,
    string? Rationale);

/// <summary>Complete the post-hoc review of a break-glass decision (approvals/0016).</summary>
/// <remarks><c>Outcome</c> is <c>Upheld</c> or <c>NotJustified</c>; the rationale is mandatory, because a
/// review that records no reasoning is a checkbox, and a checkbox is what this control already was.</remarks>
public sealed record RetrospectiveReviewRequest(string Outcome, string? Rationale);

/// <summary>
/// A row of the break-glass retrospective-review queue — the worklist projection plus the review itself.
/// </summary>
/// <remarks>
/// <para>No clinical payload, same as <see cref="WorklistItemView"/>: this is an accountability question about
/// a decision, not a question about the patient.</para>
/// <para><see cref="AgeDays"/> is carried rather than left to the client to subtract because the question asked
/// of a compliance backlog is not "how many" but "how long has the oldest been sitting there" — a count alone
/// looks the same whether it turned over yesterday or has been stuck since March.</para>
/// </remarks>
public sealed record RetrospectiveItemView(
    Guid AuthorizationId,
    string AuthNo,
    Guid BeneficiaryId,
    IReadOnlyList<string> ServiceCodes,
    string Source,
    string Status,
    DateTimeOffset? DecidedAt,
    int AgeDays,
    bool Reviewed,
    string? Outcome,
    DateTimeOffset? ReviewedAt,
    /// <summary>The reviewer's user id — a staff identity, not patient data. It is the whole point of the
    /// record: a sign-off nobody is named on cannot be asked about.</summary>
    string? ReviewedBy,
    string? Rationale)
{
    public static RetrospectiveItemView From(Authorization a, DateTimeOffset now) => new(
        a.AuthorizationId, a.AuthNo, a.BeneficiaryId, Codes.Parse(a.ServiceCodes),
        a.Source.ToString(), a.Status.ToString(), a.DecidedAt,
        (int)Math.Max(0, (now - (a.DecidedAt ?? a.SubmittedAt)).TotalDays),
        a.RetrospectiveReviewed, a.RetrospectiveOutcome, a.RetrospectiveReviewedAt,
        a.RetrospectiveReviewedBy, a.RetrospectiveRationale);
}

/// <summary>Tiny JSON helper for the <c>service_codes</c> jsonb string array (avoids a serializer dependency in
/// the projection path; the codes are simple tokens).</summary>
internal static class Codes
{
    public static string Serialize(IReadOnlyList<string> codes) =>
        System.Text.Json.JsonSerializer.Serialize(codes);

    public static IReadOnlyList<string> Parse(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }
}
