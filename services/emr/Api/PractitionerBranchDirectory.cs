using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Emr.Api;

/// <summary>
/// What provider-service knows about whether this practitioner may take an appointment at this branch on
/// this date. Both flags are nullable and mean UNKNOWN, never "no" — see
/// <see cref="UnknownPractitionerBranchDirectory"/> for why that distinction is load-bearing.
/// </summary>
/// <param name="ServesBranch">Holds an ACTIVE branch assignment covering <c>asOf</c> (18.C2 / FR-BRN-026-027).</param>
/// <param name="LicenceValid">Holds a licence that has not expired as at <c>asOf</c> (25.3 / design 42 §3).</param>
/// <param name="LicenceExpiry">The expiry date, so a refusal can say WHEN — the one fact that tells the desk
/// whether to wait for a renewal or find cover. The licence NUMBER is never returned: emr has no business
/// holding staff licence numbers.</param>
/// <param name="AssignmentValidTo">25.4 — the last day of the branch assignment, or null when it is
/// open-ended. Bounds slot generation alongside the licence: without it, three months of slots for a locum
/// whose contract ends next week look entirely healthy until the patient arrives.</param>
public sealed record PractitionerBookability(
    bool? ServesBranch, bool? LicenceValid, DateOnly? LicenceExpiry, DateOnly? AssignmentValidTo = null)
{
    public static readonly PractitionerBookability Unknown = new(null, null, null, null);
}

/// <summary>
/// Phase 18.C2 (audit R2 W7 — FR-BRN-026/027) — is this practitioner assigned to this branch?
/// Phase 25.3 (design 42 §3) — and do they hold a valid licence ON THE DATE BEING BOOKED?
///
/// provider-service has exposed <c>GET /api/v1/practitioners/{id}/serves-branch</c> since 14.5 and emr never
/// called it. There were zero <c>practitioner</c> references anywhere in emr, so a doctor could be given
/// availability at a branch they do not work at, and a patient could be booked into it. Nothing rejects the
/// booking, nothing warns the desk, and the failure surfaces as a person arriving at the wrong clinic for an
/// appointment the system confirmed — after they travelled there.
///
/// 25.3 adds the second half of the same failure. <c>license_no</c> and <c>license_expiry</c> have existed
/// since provider migration 0006 and nothing read them, so a doctor whose licence expired last year was
/// still bookable. The question is asked AS AT THE SLOT DATE rather than as at today: booking three months
/// ahead against a licence expiring next month must fail at generation, not surprise a patient on the day.
///
/// The check is a seam rather than a direct HTTP call so the booking rules stay testable without a live
/// provider-service, and so the fail-open/fail-closed decision is made in ONE place (see the null object
/// below, which is deliberately the permissive one, and why).
/// </summary>
public interface IPractitionerBranchDirectory
{
    /// <summary>Everything the booking gates need about this practitioner at this branch on this date. A
    /// field is null when the answer cannot be obtained — a caller must decide what an unknown means for its
    /// own operation rather than having "false" quietly stand in for "don't know".</summary>
    Task<PractitionerBookability> BookabilityAsync(
        Guid practitionerId, Guid branchId, DateOnly asOf, CancellationToken ct = default);
}

/// <summary>
/// The default when no provider-service is wired (unit tests, the dev harness). Answers UNKNOWN — not
/// <c>true</c> and not <c>false</c>.
///
/// Answering <c>false</c> would make every booking fail in any environment without a live sibling, which is
/// how a safety check gets removed. Answering <c>true</c> would make the check silently vacuous, which is how
/// the platform got here. Unknown forces the caller to say what it does with "don't know" — and the callers
/// below allow the operation while recording that it was UNVERIFIED, because refusing to book a patient
/// because a metadata service is briefly unavailable does more harm than the mis-assignment it guards.
/// </summary>
public sealed class UnknownPractitionerBranchDirectory : IPractitionerBranchDirectory
{
    public Task<PractitionerBookability> BookabilityAsync(
        Guid practitionerId, Guid branchId, DateOnly asOf, CancellationToken ct = default) =>
        Task.FromResult(PractitionerBookability.Unknown);
}

/// <summary>Live implementation: asks provider-service under the CALLER's bearer token, so the probe is
/// subject to the same authorization as any other read of practitioner metadata. Cached briefly — a branch
/// assignment changes on the order of weeks, and a booking screen makes this call per doctor shown. The
/// cache key carries <c>asOf</c>, because the whole point of 25.3 is that the answer differs by date.</summary>
public sealed class HttpPractitionerBranchDirectory(
    HttpClient http, IHttpContextAccessor ctx, IMemoryCache cache) : IPractitionerBranchDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PractitionerBookability> BookabilityAsync(
        Guid practitionerId, Guid branchId, DateOnly asOf, CancellationToken ct = default)
    {
        var key = $"bookability:{practitionerId}:{branchId}:{asOf:yyyy-MM-dd}";
        if (cache.TryGetValue(key, out PractitionerBookability? cached) && cached is not null) return cached;

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/practitioners/{practitionerId}/serves-branch?branchId={branchId}&asOf={asOf:yyyy-MM-dd}");
        var bearer = ctx.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return PractitionerBookability.Unknown;   // unknown, not "no"
            var dto = await resp.Content.ReadFromJsonAsync<ServesBranchDto>(Json, ct);
            if (dto is null) return PractitionerBookability.Unknown;

            var result = new PractitionerBookability(dto.ServesBranch, dto.LicenceValid, dto.LicenceExpiry, dto.AssignmentValidTo);
            cache.Set(key, result, TimeSpan.FromMinutes(5));
            return result;
        }
        catch (HttpRequestException) { return PractitionerBookability.Unknown; }
        catch (TaskCanceledException) { return PractitionerBookability.Unknown; }
    }

    private sealed record ServesBranchDto(
        Guid PractitionerId, Guid BranchId, DateOnly AsOf,
        bool ServesBranch, bool? LicenceValid, DateOnly? LicenceExpiry, bool LicenceEnforceable,
        DateOnly? AssignmentValidTo);
}
