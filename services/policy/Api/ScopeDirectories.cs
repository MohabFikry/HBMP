using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Auth;
using Mersal.Authz;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.5 — reads a caller's payer restriction from admin-service (<c>GET /api/v1/me/payers</c>),
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
public sealed class HttpPayerDirectory(HttpClient http, IHttpContextAccessor ctx, IMemoryCache cache) : IPayerDirectory
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

/// <summary>Phase 19.5 — the branch half of the same story, read from admin-service's
/// <c>GET /api/v1/me/branches</c> exactly as emr and orders already do (design 37 §2.3). Policy-service resolves
/// it ON DEMAND in member query rather than in middleware: policy administration is member-scoped
/// (all branches) by design 38 §6, so narrowing every route here would be enforcing a boundary the surface does
/// not have. An unreachable admin-service yields an empty set, which DENIES a branch-scoped caller.</summary>
public sealed class HttpBranchDirectory(HttpClient http, IHttpContextAccessor ctx, IMemoryCache cache) : IBranchDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var key = $"branches:{principal.TenantId}:{principal.Subject}";
        if (cache.TryGetValue(key, out PermittedBranches? cached) && cached is not null) return cached;

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/branches");
        var bearer = ctx.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearer["Bearer ".Length..] : bearer;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        PermittedBranches result;
        try
        {
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var dto = await resp.Content.ReadFromJsonAsync<MeBranchesDto>(Json, ct);
            result = dto is null
                ? PermittedBranches.None
                : new PermittedBranches(dto.HomeBranch, dto.PermittedBranches?.ToHashSet() ?? []);
        }
        catch (HttpRequestException) { result = PermittedBranches.None; }   // fail-closed
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { result = PermittedBranches.None; }

        cache.Set(key, result, TimeSpan.FromSeconds(60));
        return result;
    }

    private sealed record MeBranchesDto(Guid? HomeBranch, List<Guid>? PermittedBranches);
}
