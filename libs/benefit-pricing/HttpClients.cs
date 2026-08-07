using Microsoft.Extensions.Logging;
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
public sealed class HttpNetworkTierResolver(HttpClient http, ILogger<HttpNetworkTierResolver> logger)
    : INetworkTierResolver
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

        // Any other non-success is ALSO "no answer", not an exception to throw at the caller.
        //
        // `EnsureSuccessStatusCode` was here, and it meant a 403 from provider-service escaped as an
        // unhandled exception and 500'd whatever was asking — in every consumer of this library, not just the
        // one that found it. That is the wrong failure in two ways. It is louder than it should be: this
        // method's own contract is "null means the tier could not be resolved", and the callers already read
        // that as TierUnresolved and require authorization, which is the correct conservative answer. And it
        // is quieter than it should be: a 500 tells an operator nothing about WHICH dependency refused, where
        // a logged status and a fail-closed determination tell them both.
        //
        // Fail-closed direction: no tier resolved means authorization IS required. A caller can never read
        // this null as "no authorization needed".
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Network tier could not be resolved for provider {ProviderId} on {ServiceDate}: "
                + "provider-service answered {Status}. Treating the tier as unresolved, which requires "
                + "authorization.", query.ProviderId, query.ServiceDate, (int)resp.StatusCode);
            return null;
        }

        var dto = await resp.Content.ReadFromJsonAsync<TierDto>(Json, ct);
        return dto is null ? null : new ResolvedTier(dto.NetworkTierId, dto.TierCode, dto.IsOutOfNetwork, dto.Basis);
    }

    private sealed record TierDto(Guid NetworkTierId, string TierCode, bool IsOutOfNetwork, string Basis);
}

/// <summary>Reads policy-service's authored cost share for a (plan version, category, tier).</summary>
public sealed class HttpBenefitCostShareSource(HttpClient http, ILogger<HttpBenefitCostShareSource> logger)
    : IBenefitCostShareSource
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

        // Any other non-success is "no answer", exactly as in the tier resolver above — and this class had the
        // same `EnsureSuccessStatusCode` bug. Fixing only the resolver moved the 500 one call further down
        // the same method, which is what finding it a second time is worth recording: two call sites in one
        // file, one line apart in intent, and only one of them was looked at.
        //
        // The contract is already "null means no cost share could be read", and every caller turns that into
        // NotPricedAtTier — which requires authorization and refuses to quote a member. Fail-closed in both
        // the benefit sense and the operational one.
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Cost share could not be read for plan version {PlanVersionId}, category {Category}, tier "
                + "{TierId}: policy-service answered {Status}. Treating it as not priced at this tier, which "
                + "requires authorization and quotes nothing.",
                planVersionId, benefitCategoryCode, networkTierId, (int)resp.StatusCode);
            return null;
        }

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
