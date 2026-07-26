using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Auth;
using Mersal.Authz;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Emr.Api;

/// <summary>Reads a caller's permitted branch set from admin-service (<c>GET /api/v1/me/branches</c>),
/// forwarding the bearer so the downstream resolves the same principal (design 37 §2.3). Cached per user for
/// a short window (≤60s) to avoid a round-trip on every request; a revocation takes effect within the TTL.
/// A failure to reach admin returns an EMPTY set — fail-closed: a BranchScoped caller is then denied.</summary>
public sealed class HttpBranchDirectory(HttpClient http, IHttpContextAccessor ctx, IMemoryCache cache) : IBranchDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PermittedBranches> GetAsync(HbmpPrincipal principal, CancellationToken ct = default)
    {
        var key = $"branches:{principal.TenantId}:{principal.Subject}";
        if (cache.TryGetValue(key, out PermittedBranches? cached) && cached is not null) return cached;

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/branches");
        var bearer = ctx.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
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

        cache.Set(key, result, TimeSpan.FromSeconds(60));
        return result;
    }

    private sealed record MeBranchesDto(Guid? HomeBranch, List<Guid>? PermittedBranches);
}
