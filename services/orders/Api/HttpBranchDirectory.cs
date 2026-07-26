using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Auth;
using Mersal.Authz;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Orders.Api;

/// <summary>Reads a caller's permitted branch set from admin-service (<c>GET /api/v1/me/branches</c>),
/// bearer-forwarded, 60s-cached, fail-closed (design 37 §2.3). Twin of the emr-service directory — the
/// clinician-side order worklist is branch-scoped; the provider fulfillment queue (5.1) is NOT.</summary>
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
            result = dto is null ? PermittedBranches.None : new PermittedBranches(dto.HomeBranch, dto.PermittedBranches?.ToHashSet() ?? []);
        }
        catch (HttpRequestException) { result = PermittedBranches.None; }

        cache.Set(key, result, TimeSpan.FromSeconds(60));
        return result;
    }

    private sealed record MeBranchesDto(Guid? HomeBranch, List<Guid>? PermittedBranches);
}
