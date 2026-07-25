using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;

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
    AuthPriority Priority);

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
    IReadOnlyList<ClinicalNote> Notes,
    IReadOnlyList<SupportingDocument> Documents)
{
    public static ReviewView From(Authorization a, ClinicalContext? ctx) => new(
        a.AuthorizationId, a.AuthNo, a.BeneficiaryId, a.Source.ToString(), a.SourceRef,
        Codes.Parse(a.ServiceCodes), a.RequestedScope, a.Priority.ToString(), a.Status.ToString(),
        ctx is not null,
        ctx?.EmrSummary ?? "clinical context unavailable",
        ctx?.Notes ?? [],
        ctx?.Documents ?? []);
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
