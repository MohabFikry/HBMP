using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Emr.Api;

/// <summary>
/// Phase 18.C2 (audit R2 W7 — FR-BRN-026/027) — is this practitioner assigned to this branch?
///
/// provider-service has exposed <c>GET /api/v1/practitioners/{id}/serves-branch</c> since 14.5 and emr never
/// called it. There were zero <c>practitioner</c> references anywhere in emr, so a doctor could be given
/// availability at a branch they do not work at, and a patient could be booked into it. Nothing rejects the
/// booking, nothing warns the desk, and the failure surfaces as a person arriving at the wrong clinic for an
/// appointment the system confirmed — after they travelled there.
///
/// The check is a seam rather than a direct HTTP call so the booking rules stay testable without a live
/// provider-service, and so the fail-open/fail-closed decision is made in ONE place (see the null object
/// below, which is deliberately the permissive one, and why).
/// </summary>
public interface IPractitionerBranchDirectory
{
    /// <summary>True when <paramref name="practitionerId"/> holds an ACTIVE assignment to
    /// <paramref name="branchId"/>. Null when the answer cannot be obtained — a caller must decide what an
    /// unknown means for its own operation rather than having "false" quietly stand in for "don't know".</summary>
    Task<bool?> ServesBranchAsync(Guid practitionerId, Guid branchId, CancellationToken ct = default);
}

/// <summary>
/// The default when no provider-service is wired (unit tests, the dev harness). Answers <c>null</c> — not
/// <c>true</c> and not <c>false</c>.
///
/// Answering <c>false</c> would make every booking fail in any environment without a live sibling, which is
/// how a safety check gets removed. Answering <c>true</c> would make the check silently vacuous, which is how
/// the platform got here. <c>null</c> forces the caller to say what it does with "unknown" — and the callers
/// below allow the operation while recording that it was UNVERIFIED, because refusing to book a patient
/// because a metadata service is briefly unavailable does more harm than the mis-assignment it guards.
/// </summary>
public sealed class UnknownPractitionerBranchDirectory : IPractitionerBranchDirectory
{
    public Task<bool?> ServesBranchAsync(Guid practitionerId, Guid branchId, CancellationToken ct = default) =>
        Task.FromResult<bool?>(null);
}

/// <summary>Live implementation: asks provider-service under the CALLER's bearer token, so the probe is
/// subject to the same authorization as any other read of practitioner metadata. Cached briefly — a branch
/// assignment changes on the order of weeks, and a booking screen makes this call per doctor shown.</summary>
public sealed class HttpPractitionerBranchDirectory(
    HttpClient http, IHttpContextAccessor ctx, IMemoryCache cache) : IPractitionerBranchDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool?> ServesBranchAsync(Guid practitionerId, Guid branchId, CancellationToken ct = default)
    {
        var key = $"serves-branch:{practitionerId}:{branchId}";
        if (cache.TryGetValue(key, out bool cached)) return cached;

        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/practitioners/{practitionerId}/serves-branch?branchId={branchId}");
        var bearer = ctx.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;      // unknown, not "no"
            var dto = await resp.Content.ReadFromJsonAsync<ServesBranchDto>(Json, ct);
            if (dto is null) return null;
            cache.Set(key, dto.ServesBranch, TimeSpan.FromMinutes(5));
            return dto.ServesBranch;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    private sealed record ServesBranchDto(Guid PractitionerId, Guid BranchId, bool ServesBranch);
}
