using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>Forwards contact edits to patient-service under the caller's bearer (patient-service owns the
/// one-primary rule + history). Responses pass through faithfully.</summary>
public sealed class HttpContactGateway(IHttpClientFactory factory) : IContactGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<GatewayResult> UpdateContactAsync(Guid ben, Guid contactId, object body, string? bearer, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"/api/v1/beneficiaries/{ben}/contacts/{contactId}", body, bearer, ct);

    public Task<GatewayResult> AddContactAsync(Guid ben, object body, string? bearer, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/api/v1/beneficiaries/{ben}/contacts", body, bearer, ct);

    private async Task<GatewayResult> SendAsync(HttpMethod method, string path, object body, string? bearer, CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient("patient");
            using var req = new HttpRequestMessage(method, path);
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return new GatewayResult((int)resp.StatusCode, text, null);
        }
        catch (HttpRequestException) { return new GatewayResult(502, null, null); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new GatewayResult(504, null, null); }
    }
}
