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
    long TatElapsedSeconds)
{
    public static WorklistItemView From(Authorization a, DateTimeOffset now) => new(
        a.AuthorizationId, a.AuthNo, a.BeneficiaryId,
        Codes.Parse(a.ServiceCodes), a.Priority.ToString(), a.Status.ToString(),
        a.SlaDueAt, a.SlaBreached,
        (long)((a.DecidedAt ?? now) - a.SubmittedAt).TotalSeconds);
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
