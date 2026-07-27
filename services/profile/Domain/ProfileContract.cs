using Mersal.Authz;

namespace Mersal.Profile.Domain;

// Phase 20 — the wire contract of the unified patient profile (design 39 §3, build prompt 20.1).
//
// THE SHAPE IS THE CONTROL. `Data` is null for every state except Visible, and the service serializes with
// JsonIgnoreCondition.WhenWritingNull, so a withheld section's content is ABSENT from the JSON rather than
// present-and-empty. That distinction is the whole feature: a payload that carries the field and trusts the
// browser not to render it is the aggregation vulnerability design 39 §1 exists to prevent.

/// <summary>The composed profile. Sections arrive in design-39 §3 render order; a section the caller may never
/// see is not in the list at all (the `—` column of §4).</summary>
public sealed record PatientProfile(
    Guid BeneficiaryId,
    DateTimeOffset ServedAt,
    IReadOnlyList<ProfileSection> Sections);

/// <summary>
/// One independently-gated section.
///
/// <para><see cref="State"/> is a string on the wire rather than an int so a new state cannot silently shift
/// the meaning of an existing one, and so the SPA's three-state rendering reads as three names.</para>
/// </summary>
public sealed record ProfileSection
{
    public required string Key { get; init; }

    /// <summary>Visible | Restricted | NotApplicable | Unavailable — three of which are NOT the same thing.</summary>
    public required string State { get; init; }

    /// <summary>Why content was withheld (<c>not-treating</c>, <c>sensitive-requires-grant</c>, …) or why the
    /// section is unavailable. Null on a Visible section.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Where offered, the action that would obtain access — wired to the design-37 §6 request flow for
    /// sensitive results. Present only when requesting access is actually possible.</summary>
    public RequestAccessAction? RequestAccessAction { get; init; }

    /// <summary>The section payload. <b>Null unless <see cref="State"/> is Visible</b>, and omitted from the
    /// serialized JSON when null.</summary>
    public object? Data { get; init; }

    public static ProfileSection Visible(string key, object? data) =>
        new() { Key = key, State = nameof(ProfileSectionState.Visible), Data = data };

    public static ProfileSection Restricted(string key, string reasonCode, RequestAccessAction? action = null) =>
        new()
        {
            Key = key, State = nameof(ProfileSectionState.Restricted),
            ReasonCode = reasonCode, RequestAccessAction = action,
        };

    public static ProfileSection NotApplicable(string key) =>
        new() { Key = key, State = nameof(ProfileSectionState.NotApplicable) };

    /// <summary>The owning service failed or timed out. Deliberately distinct from an empty Visible section: a
    /// clinician who reads "no allergies recorded" when the truth is "emr did not answer" has been actively
    /// misinformed, which is worse than being told nothing.</summary>
    public static ProfileSection Unavailable(string key, string reasonCode = "upstream-unavailable") =>
        new() { Key = key, State = nameof(ProfileSectionState.Unavailable), ReasonCode = reasonCode };
}

/// <summary>An offered route out of a Restricted state — rendered as the "Request access" control.</summary>
public sealed record RequestAccessAction(string Kind, string Href, string? Label = null)
{
    /// <summary>The design-37 §6 single-result release request (phase 14.8), which orders-service decides.</summary>
    public static RequestAccessAction SensitiveResult(Guid beneficiaryId) =>
        new("report-access-request", $"/api/v1/report-access-requests?beneficiaryId={beneficiaryId}",
            "Request access");
}
