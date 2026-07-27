using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// Phase 19.1b — policy-service's read-only window onto the tier catalogue owned by provider-service.
///
/// Deliberately narrow: an id and a code, nothing else. policy administration needs to know WHICH tiers exist
/// so it can price every one of them; it has no business knowing a tier's rank, description or which providers
/// sit in it. Modelling more here would also be the first step towards policy-service believing it owns tiers,
/// which is exactly the separation 19.1b exists to create.
/// </summary>
public interface INetworkTierCatalog
{
    /// <summary>The Active tiers, which is the set a plan version must price completely before it can be
    /// activated. Forwards the caller's bearer so the read is authorized as them, not as the service.</summary>
    Task<IReadOnlyList<NetworkTierRef>> ActiveTiersAsync(string? bearerToken, CancellationToken ct = default);
}

/// <summary>Reads <c>GET /api/v1/network-tiers?status=Active</c> from provider-service, forwarding the caller's
/// token (the same shape provider-service itself uses to reach masterdata).</summary>
public sealed class HttpNetworkTierCatalog(HttpClient http) : INetworkTierCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<NetworkTierRef>> ActiveTiersAsync(string? bearerToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/network-tiers?status=Active");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await http.SendAsync(req, ct);
        // NOT fail-soft. An empty catalogue would make the completeness check vacuous and let a version
        // activate with no cost share priced at all — the precise outcome the check exists to prevent. A
        // network-tier catalogue that cannot be read is a reason to refuse activation, not to skip it.
        resp.EnsureSuccessStatusCode();
        var rows = await resp.Content.ReadFromJsonAsync<TierDto[]>(Json, ct) ?? [];
        return [.. rows.Select(r => new NetworkTierRef(r.NetworkTierId, r.TierCode))];
    }

    private sealed record TierDto(Guid NetworkTierId, string TierCode);
}
