using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.BenefitPricing;

/// <summary>Reads provider-service's tier resolver, forwarding the caller's bearer so the resolution is
/// authorized as them rather than as the service.</summary>
public sealed class HttpNetworkTierResolver(HttpClient http) : INetworkTierResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ResolvedTier?> ResolveAsync(TierQuery query, string? bearerToken, CancellationToken ct = default)
    {
        var url = $"/api/v1/network-tiers/resolve?providerId={query.ProviderId}" +
                  $"&serviceDate={query.ServiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                  (query.LocationId is { } loc ? $"&locationId={loc}" : "") +
                  (string.IsNullOrWhiteSpace(query.ServiceCode) ? "" : $"&serviceCode={Uri.EscapeDataString(query.ServiceCode)}");

        using var req = BearerRequest.Get(url, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        // 404 = unknown provider, 409 = no out-of-network tier configured to fall back to. Both mean "no
        // answer", and both are surfaced as such rather than defaulting to in-network — which is the one
        // wrong answer that silently pays a provider nobody negotiated with.
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return null;
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<TierDto>(Json, ct);
        return dto is null ? null : new ResolvedTier(dto.NetworkTierId, dto.TierCode, dto.IsOutOfNetwork, dto.Basis);
    }

    private sealed record TierDto(Guid NetworkTierId, string TierCode, bool IsOutOfNetwork, string Basis);
}

/// <summary>Reads policy-service's authored cost share for a (plan version, category, tier).</summary>
public sealed class HttpBenefitCostShareSource(HttpClient http) : IBenefitCostShareSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BenefitCostShare?> GetAsync(
        Guid planVersionId, string benefitCategoryCode, Guid networkTierId, string? bearerToken, CancellationToken ct = default)
    {
        var url = $"/api/v1/plan-versions/{planVersionId}/cost-share" +
                  $"?benefitCategoryCode={Uri.EscapeDataString(benefitCategoryCode)}&networkTierId={networkTierId}";

        using var req = BearerRequest.Get(url, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<BenefitCostShare>(Json, ct);
    }
}

internal static class BearerRequest
{
    public static HttpRequestMessage Get(string url, string? bearerToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }
}

public static class BenefitPricingServiceCollectionExtensions
{
    /// <summary>Wire the shared tier-pricing path into a consumer service (approvals, eligibility, claims).</summary>
    public static IServiceCollection AddHbmpTierPricing(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        services.AddHttpClient<INetworkTierResolver, HttpNetworkTierResolver>(c =>
            c.BaseAddress = new Uri(config["Provider:BaseUrl"] ?? "http://provider-service:8080"));
        services.AddHttpClient<IBenefitCostShareSource, HttpBenefitCostShareSource>(c =>
            c.BaseAddress = new Uri(config["Policy:BaseUrl"] ?? "http://policy-service:8080"));
        services.AddScoped<TierPricingService>();
        return services;
    }
}
