using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Orders.Infrastructure;

namespace Mersal.Orders.Api;

/// <summary>Asks emr-service (the authority over encounters) whether the caller has a treating relationship with
/// a beneficiary, forwarding the caller's bearer token so emr evaluates it for the SAME principal. Min-necessary:
/// only a boolean crosses the wire. Fails closed (no treating relationship) if emr is unreachable.</summary>
public sealed class HttpTreatingRelationshipClient(HttpClient http) : ITreatingRelationshipClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> TreatsAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/treating-relationship?beneficiaryId={beneficiaryId}");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;   // fail closed
        var body = await resp.Content.ReadFromJsonAsync<TreatsDto>(Json, ct);
        return body?.Treats ?? false;
    }

    private sealed record TreatsDto(bool Treats);
}
