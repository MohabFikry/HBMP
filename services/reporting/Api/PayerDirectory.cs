using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Auth;
using Mersal.Authz;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Reporting.Api;

// Phase 19.6b — the same payer-scope resolution policy-service uses, in reporting-service.
//
// COPIED RATHER THAN SHARED, deliberately and narrowly. The CLIENT is nine lines of HTTP plumbing; the RULE it
// enforces — fail closed, because payer scope's empty set means unrestricted — lives once in libs/authz
// (PermittedPayers.DenyAll) and is what both callers depend on. Promoting the HttpClient itself into a shared
// library would drag Microsoft.Extensions.Http and IHttpContextAccessor into every consumer of libs/authz,
// including the ones with no HTTP surface at all.

/// <summary>
/// Reads a caller's payer restriction from admin-service (<c>GET /api/v1/me/payers</c>),
/// forwarding the bearer so the downstream resolves the same principal.
///
/// <para>FAIL-CLOSED, AND NOTE WHICH DIRECTION THAT IS. Branch scope fails closed by returning an empty
/// permitted set, which DENIES. Payer scope's empty set means "unrestricted", so returning it on an error
/// would fail OPEN — an admin-service outage would silently hand every payer's book of business to a user
/// restricted to one. So a failure returns <see cref="PermittedPayers.DenyAll"/>: restricted to nothing.</para>
///
/// <para>Cached per user for ≤60s, matching the branch directory. A revocation takes effect within the TTL,
/// which is the same trade every scope resolution in the platform makes: a round trip per request would put
/// admin-service on the critical path of every query.</para>
/// </summary>
public sealed class ReportingPayerDirectory(HttpClient http, IHttpContextAccessor ctx, IMemoryCache cache) : IPayerDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PermittedPayers> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var key = $"payers:{principal.TenantId}:{principal.Subject}";
        if (cache.TryGetValue(key, out PermittedPayers? cached) && cached is not null) return cached;

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/payers");
        var bearer = ctx.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearer["Bearer ".Length..] : bearer;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        PermittedPayers result;
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return PermittedPayers.DenyAll;   // not cached — an outage is transient
            var dto = await resp.Content.ReadFromJsonAsync<MePayersDto>(Json, ct);
            result = dto is null || dto.Unrestricted
                ? PermittedPayers.Unrestricted
                : PermittedPayers.RestrictedTo(dto.PayerIds ?? []);
        }
        catch (HttpRequestException) { return PermittedPayers.DenyAll; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return PermittedPayers.DenyAll; }

        cache.Set(key, result, TimeSpan.FromSeconds(60));
        return result;
    }

    private sealed record MePayersDto(bool Unrestricted, List<Guid>? PayerIds);
}
