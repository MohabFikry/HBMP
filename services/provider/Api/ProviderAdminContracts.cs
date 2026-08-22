using System.Text.Json;
using Mersal.Provider.Domain;

namespace Mersal.Provider.Api;

/// <summary>
/// Phase 19.9 — the request and response shapes for administering a provider (design 58).
///
/// <para>Everything here answers one of four questions an operator has in front of a provider record: what
/// is it, what is stopping it going live, what has changed, and who is attached to it. The reads are
/// deliberately separate from <see cref="ProviderView"/>, which is the routing-and-picker projection half
/// the platform depends on and is not the place to grow eleven administrative fields.</para>
/// </summary>

// ── writes ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Edit a provider's identity. <c>ProviderCode</c> is carried and CHECKED rather than ignored: it is what a
/// claim, a contract and an invoice cite, so it cannot change — and a form that silently discards a
/// corrected code is worse than one that refuses it, because the operator walks away believing it took.
/// </summary>
public sealed record UpdateProvider(
    string ProviderCode, string LegalName, string ProviderType,
    string? CommercialName, string? TaxId, string? Phone, string? Email, string? Notes);

public sealed record UpdateLocation(
    string Name, string? Governorate, string? Address, decimal? GeoLat, decimal? GeoLng);

/// <summary>Every deactivation carries a reason of at least ten characters — the same bar the policy portal
/// holds, and for the same reason: "old" is indistinguishable from no reason at all a year later.</summary>
public sealed record DeactivateWithReason(string Reason);

public sealed record UpdateContract(string ContractNo, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record UpdateServiceLine(decimal AgreedPrice, string? CurrencyCode);

public sealed record UpdateCredential(
    string CredentialType, string Status, DateOnly? ValidFrom, DateOnly? ValidTo, Guid? DocumentId, bool IsMandatory);

// ── reads ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What is stopping this provider going live, as four facts rather than one refusal string.
///
/// <para>The activation endpoint has always answered a blocked attempt with 422 and a sentence ("Cannot
/// activate: no active contract"). That tells an operator the first thing that is wrong, one attempt at a
/// time, and only after they have pressed the button. The guard evaluates four conditions; returning all
/// four lets the screen show the checklist BEFORE the attempt, which is the difference between a workflow
/// and a guessing game.</para>
/// </summary>
public sealed record ReadinessView(
    bool HasPrimaryLocation,
    bool HasMandatoryCredentials,
    bool MandatoryCredentialsValid,
    bool HasActiveContract,
    bool CanActivate,
    string? BlockingReason);

/// <summary>An open, not-yet-approved termination. Rendered so the second approver knows what they are
/// walking into, and so the requester can see their own request is still sitting there.</summary>
public sealed record PendingTerminationView(
    Guid RequestId, string Reason, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>How much hangs off this provider. Counts only — the sections themselves are separate reads.</summary>
public sealed record ProviderBookView(
    int Locations, int Contracts, int ActiveContracts, int Credentials, int ActiveUsers);

public sealed record ProviderDetailView(
    Guid ProviderId, string ProviderCode, string LegalName, string ProviderType, string ProviderTypeLabel,
    string Status, string OnboardingState,
    string? CommercialName, string? TaxId, string? Phone, string? Email, string? Notes,
    string? StatusReason, string? StatusActorName, DateTimeOffset? StatusChangedAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? CreatedByName, string? UpdatedByName,
    ReadinessView Readiness, PendingTerminationView? PendingTermination, ProviderBookView Book,
    /// <summary>
    /// The provider-scoped roles THIS caller may grant, computed by asking
    /// <see cref="Mersal.Provider.Domain.ProviderUserRules.CanProvision"/> about each one.
    ///
    /// <para>Returned rather than hardcoded in the client for the reason this codebase keeps re-learning: a
    /// second copy of a vocabulary drifts from the first. The list is also caller-dependent — a Provider
    /// Admin may grant the tech roles and not their own — so a static picker would offer an option that
    /// exists only to be refused, which is the thing the portal's own conventions forbid.</para>
    /// </summary>
    IReadOnlyList<string> ProvisionableRoles);

public sealed record LocationView(
    Guid LocationId, string Name, string? Governorate, string? Address,
    decimal? GeoLat, decimal? GeoLng, bool IsPrimary, bool IsDeleted,
    string? DeactivationReason, DateTimeOffset? DeactivatedAt);

public sealed record ContractView(
    Guid ContractId, string ContractNo, string Status, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    int ServiceLines, bool InEffect, string? StatusReason, string? StatusActorName, DateTimeOffset? StatusChangedAt);

/// <summary>A priced line. <c>AgreedPrice</c> is T2 financial and is <b>null</b> — the whole field, not a
/// zero — for a caller without <c>provider:finance</c>. A zero would read as "free", which is a different
/// and much worse claim than "you are not being shown this".</summary>
public sealed record ServiceLineView(
    Guid ServiceLineId, string ServiceType, string CodeSystem, string Code,
    decimal? AgreedPrice, string? CurrencyCode);

public sealed record CredentialView(
    Guid CredentialId, string CredentialType, string Status, DateOnly? ValidFrom, DateOnly? ValidTo,
    Guid? DocumentId, bool IsMandatory, bool IsDeleted, bool ValidToday, int? DaysUntilExpiry);

public sealed record ProviderUserView(
    Guid UserId, string SubjectRef, string Role, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>One entry of a change timeline, projected from a trigger's jsonb snapshot.</summary>
/// <remarks>
/// <para><b>Values, not diffs</b> — the client renders "before → after" by comparing an entry with the one
/// before it, so the diff is written once for the whole platform (this is the same choice
/// <see cref="PractitionerHistoryView"/> made, and the client-side renderer is shared).</para>
/// <para><b>Projected, not returned verbatim.</b> The snapshot is the entire row. Only the administered
/// fields are lifted out: a timeline is a record of changes, not a second route to the record itself, and
/// returning the row whole would make it one — including for a caller whose projection of the LIVE record
/// withholds something.</para>
/// </remarks>
public sealed record AdminHistoryView(
    long HistoryId, string Operation, DateTimeOffset RecordedAt,
    string? ActorSubject, string? ActorName, string? StatusReason,
    IReadOnlyDictionary<string, string?> Fields);

/// <summary>
/// Reads a trigger snapshot defensively.
///
/// <para>Every accessor tolerates an absent property, because a snapshot is whatever the table looked like
/// the day it was written: an entry from before 0015 simply has no <c>status_reason</c>. Throwing on that
/// would make the timeline unreadable for exactly the oldest entries — the ones somebody is digging for.</para>
/// </summary>
public static class Snapshot
{
    public static AdminHistoryView Project(
        long id, string operation, DateTimeOffset at, string json, params string[] fields)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var picked = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var f in fields) picked[f] = Value(r, f);
        return new AdminHistoryView(
            id, operation, at,
            Text(r, "updated_by") ?? Text(r, "created_by"),
            Text(r, "updated_by_name") ?? Text(r, "status_actor_name") ?? Text(r, "created_by_name"),
            Text(r, "status_reason") ?? Text(r, "deactivation_reason"),
            picked);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Any scalar as its display string. Booleans and numbers are rendered rather than skipped:
    /// <c>is_primary</c> flipping is one of the changes this timeline exists to show.</summary>
    private static string? Value(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}
