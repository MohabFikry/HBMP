using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace Mersal.Orders.Api;

/// <summary>Validates order-line codes against masterdata-service (phase 0b), forwarding the caller's bearer
/// token, caching positive lookups. CPT resolves via <c>/cpt-codes/{code}/exists</c>; LOINC has no dataset yet
/// so it is accepted-and-recorded (documented, as in provider-service); LOCAL is free text. FAILS CLOSED on
/// transport/5xx so an unvalidated code is never persisted.</summary>
public sealed class HttpCodeValidator(HttpClient http, IMemoryCache cache) : ICodeValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public async Task<bool> IsValidAsync(CodeSystem system, string code, string? bearerToken, CancellationToken ct = default)
    {
        if (system is CodeSystem.LOCAL or CodeSystem.LOINC) return true;   // LOCAL free; LOINC not yet in masterdata
        if (string.IsNullOrWhiteSpace(code)) return false;

        var cacheKey = $"cpt:{code}";
        if (cache.TryGetValue<bool>(cacheKey, out var ok) && ok) return true;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cpt-codes/{Uri.EscapeDataString(code)}/exists");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ExistsDto>(Json, ct);
        var exists = body?.Exists ?? false;
        if (exists) cache.Set(cacheKey, true, Ttl);
        return exists;
    }

    private sealed record ExistsDto(bool Exists);
}
