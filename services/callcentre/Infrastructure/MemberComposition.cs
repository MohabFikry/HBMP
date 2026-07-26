using Mersal.CallCentre.Domain;

namespace Mersal.CallCentre.Infrastructure;

/// <summary>The member-composition seam (phase 15.2). callcentre-service AGGREGATES sibling services (eligibility
/// reception search for identity + coverage/limits, emr for appointments across all branches, patient for contacts,
/// pharmacy for referrals) under the caller's bearer token — it does NOT copy their data. The HTTP implementation
/// lives in the Api layer; tests inject a fake. Both methods return CLINICAL-FREE projections by construction
/// (<see cref="Member360"/> / <see cref="MemberMatch"/> have no field that can carry a diagnosis/result/etc.).</summary>
public interface IMemberDirectory
{
    /// <summary>Pre-verification search — thin by design: only name + id + challengeable identifier TYPES.</summary>
    Task<MemberSearchResult> SearchAsync(string query, string? bearerToken, CancellationToken ct = default);

    /// <summary>Post-verification 360 — the composed, projected view across ALL branches. Null if the member
    /// cannot be resolved (the endpoint then 404s); a partially-unreachable sibling degrades to an empty section,
    /// never fabricated data.</summary>
    Task<Member360?> AssembleAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default);
}
