using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;

namespace Mersal.Emr.Api;

/// <summary>
/// Reads member status from eligibility-service (2.1) over HTTP, forwarding the caller's bearer token so
/// the downstream authorizes the same principal. A missing/blocked member surfaces as null → the gate
/// blocks the visit (23-state-machines §1).
/// </summary>
public sealed class HttpMemberStatusProvider(HttpClient http) : IMemberStatusProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MemberStatus?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/eligibility/members/{beneficiaryId}/status");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<StatusDto>(Json, ct);
        return body is not null && Enum.TryParse<MemberStatus>(body.Status, out var s) ? s : null;
    }

    private sealed record StatusDto(Guid BeneficiaryId, string Status, string? MemberNo);
}
