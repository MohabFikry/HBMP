using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Emr.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Emr.Api;

/// <summary>Validates clinical codes against masterdata-service (phase 0b), forwarding the caller's bearer
/// token, and caches positive lookups in-process (masterdata codes are immutable within a deployment). Writes
/// FAIL CLOSED: a transport/5xx error propagates so the endpoint rejects the write rather than persisting an
/// unvalidated code. LOINC has no dataset loaded yet, so a present code is accepted-and-recorded (documented,
/// as in provider-service) rather than falsely rejected.</summary>
public sealed class HttpClinicalCodeValidator(HttpClient http, IMemoryCache cache) : IClinicalCodeValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public Task<bool> IcdExistsAsync(string icdCode, string? bearerToken, CancellationToken ct = default) =>
        ExistsAsync($"icd:{icdCode}", $"/api/v1/icd-codes/{Uri.EscapeDataString(icdCode)}/exists", bearerToken, ct);

    /// <summary>Resolves the allergen and returns its name (null = not in master data). Same fail-closed rule
    /// as the existence checks: a 5xx or transport error propagates, so the write is rejected rather than
    /// persisted with an unvalidated allergen and no name.</summary>
    public async Task<string?> AllergenNameAsync(Guid allergenId, string? bearerToken, CancellationToken ct = default)
    {
        var cacheKey = $"allergen-name:{allergenId}";
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null) return cached;

        using var req = Authorized(HttpMethod.Get, $"/api/v1/allergens/{allergenId}", bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();   // fail closed on 5xx/transport
        var body = await resp.Content.ReadFromJsonAsync<AllergenDto>(Json, ct);
        var name = string.IsNullOrWhiteSpace(body?.Name) ? null : body!.Name;
        if (name is not null) cache.Set(cacheKey, name, Ttl);   // cache only hits (immutable master data)
        return name;
    }

    public Task<bool> DrugExistsAsync(Guid drugId, string? bearerToken, CancellationToken ct = default) =>
        ExistsAsync($"drug:{drugId}", $"/api/v1/drugs/by-id/{drugId}/exists", bearerToken, ct);

    public Task<bool> LoincValidAsync(string? loincCode, string? bearerToken, CancellationToken ct = default) =>
        Task.FromResult(true);   // optional; no LOINC dataset yet → accepted-and-recorded when present

    private async Task<bool> ExistsAsync(string cacheKey, string path, string? bearerToken, CancellationToken ct)
    {
        if (cache.TryGetValue<bool>(cacheKey, out var cached) && cached) return true;

        using var req = Authorized(HttpMethod.Get, path, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        resp.EnsureSuccessStatusCode();   // fail closed on 5xx/transport — endpoint rejects the write
        var body = await resp.Content.ReadFromJsonAsync<ExistsDto>(Json, ct);
        var exists = body?.Exists ?? false;
        if (exists) cache.Set(cacheKey, true, Ttl);   // cache only positives (immutable master data)
        return exists;
    }

    /// <summary>One request, with the caller's bearer token forwarded — masterdata applies `masterdata:read`
    /// to the USER, not to emr-service, so a token dropped here becomes a 401 that reads like an outage.</summary>
    private static HttpRequestMessage Authorized(HttpMethod method, string path, string? bearerToken)
    {
        var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    private sealed record ExistsDto(bool Exists);
    private sealed record AllergenDto(Guid AllergenId, string? Code, string? Name);
}
