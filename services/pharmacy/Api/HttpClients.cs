using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Pharmacy.Api;

internal static class BearerHeader
{
    public static void Apply(HttpRequestMessage req, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return;
        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..] : bearerToken;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

/// <summary>Validates a prescription-line drug id against masterdata (fail-closed on writes), caching positives.</summary>
public sealed class HttpDrugValidator(HttpClient http, IMemoryCache cache) : IDrugValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> DrugExistsAsync(Guid drugId, string? bearerToken, CancellationToken ct = default)
    {
        var key = $"drug:{drugId}";
        if (cache.TryGetValue<bool>(key, out var ok) && ok) return true;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/drugs/by-id/{drugId}/exists");
        BearerHeader.Apply(req, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ExistsDto>(Json, ct);
        var exists = body?.Exists ?? false;
        if (exists) cache.Set(key, true, TimeSpan.FromMinutes(30));
        return exists;
    }

    private sealed record ExistsDto(bool Exists);
}

/// <summary>Advisory prescribe-time screening (US-033): drug-interaction across the Rx's drug ids (masterdata) and
/// allergy conflicts vs the beneficiary's allergies (sourced from emr-service, checked in masterdata). Best-effort
/// and NON-BLOCKING — any transport failure yields no alert rather than blocking the prescription.</summary>
public sealed class HttpPrescribingScreener(IHttpClientFactory factory) : IPrescribingScreener
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AlertScreening> ScreenAsync(Guid beneficiaryId, IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct = default)
    {
        var screening = new AlertScreening();
        var masterdata = factory.CreateClient("masterdata");
        var emr = factory.CreateClient("emr");

        // 1) Drug-drug interactions among the prescribed drugs.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/drug-interactions/check-by-ids")
            { Content = JsonContent.Create(new { drugIds }) };
            BearerHeader.Apply(req, bearerToken);
            using var resp = await masterdata.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<InteractionDto>(Json, ct);
                if (body?.HighestSeverity is { } sev)
                    screening.AddInteraction(sev, $"{body.Interactions?.Length ?? 0} interaction(s) among prescribed drugs (highest: {sev}).");
            }
        }
        catch (HttpRequestException) { /* advisory — ignore */ }

        // 2) Allergy conflicts: pull the beneficiary's allergen ids from emr, screen each drug in masterdata.
        Guid[] allergenIds;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/beneficiaries/{beneficiaryId}/allergies");
            BearerHeader.Apply(req, bearerToken);
            using var resp = await emr.SendAsync(req, ct);
            allergenIds = resp.IsSuccessStatusCode
                ? (await resp.Content.ReadFromJsonAsync<AllergyDto[]>(Json, ct) ?? []).Select(a => a.AllergenId).ToArray()
                : [];
        }
        catch (HttpRequestException) { allergenIds = []; }

        if (allergenIds.Length > 0)
        {
            foreach (var drugId in drugIds.Distinct())
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/allergies/check-by-ids")
                    { Content = JsonContent.Create(new { drugId, allergenIds }) };
                    BearerHeader.Apply(req, bearerToken);
                    using var resp = await masterdata.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadFromJsonAsync<AllergyCheckDto>(Json, ct);
                        if (body?.Conflict == true)
                            screening.AddAllergy($"Drug {drugId} conflicts with a recorded allergy ({body.MatchedOn}).");
                    }
                }
                catch (HttpRequestException) { /* advisory — ignore */ }
            }
        }

        return screening;
    }

    private sealed record InteractionDto(string? HighestSeverity, object[]? Interactions);
    private sealed record AllergyDto(Guid AllergenId);
    private sealed record AllergyCheckDto(bool Conflict, string? MatchedOn);
}

/// <summary>Treating-relationship check via emr-service (token forwarded, boolean only). Fails closed.</summary>
public sealed class HttpTreatingRelationshipClient(HttpClient http) : ITreatingRelationshipClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> TreatsAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/treating-relationship?beneficiaryId={beneficiaryId}");
        BearerHeader.Apply(req, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;
        var body = await resp.Content.ReadFromJsonAsync<TreatsDto>(Json, ct);
        return body?.Treats ?? false;
    }

    private sealed record TreatsDto(bool Treats);
}
