using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;

namespace Mersal.Provider.Api;

/// <summary>Validates service-line codes against masterdata-service (phase 0b), forwarding the caller's
/// bearer token. CPT resolves via <c>/cpt-codes/{code}/exists</c>. LOINC has no dataset loaded yet, so it
/// is accepted-and-recorded (documented deviation) rather than falsely rejected. LOCAL is always free.</summary>
public sealed class HttpCodeValidator(HttpClient http) : ICodeValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsValidAsync(CodeSystem system, string code, string? bearerToken, CancellationToken ct = default)
    {
        if (system is CodeSystem.LOCAL or CodeSystem.LOINC) return true;   // LOCAL free; LOINC not yet in masterdata

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
        return body?.Exists ?? false;
    }

    private sealed record ExistsDto(string Code, bool Exists);
}
