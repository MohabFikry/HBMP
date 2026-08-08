using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mersal.CallCentre.Infrastructure;

namespace Mersal.CallCentre.Api;

/// <summary>Delegates appointment actions to the emr engine over HTTP, forwarding the caller's bearer, the
/// Idempotency-Key and the If-Match ETag verbatim — so the phase-3 no-double-book guarantee, idempotent replay, and
/// optimistic concurrency all live in emr and are preserved unchanged. Responses are passed through faithfully.</summary>
public sealed class HttpAppointmentGateway(IHttpClientFactory factory) : IAppointmentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<GatewayResult> SearchSlotsAsync(string queryString, string? bearer, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"/api/v1/appointment-slots{queryString}", null, bearer, null, null, ct);

    public Task<GatewayResult> BookAsync(object body, string? bearer, string? idem, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/v1/appointments", body, bearer, idem, null, ct);

    public Task<GatewayResult> RescheduleAsync(Guid id, object body, string? bearer, string? idem, string? ifMatch, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/api/v1/appointments/{id}/reschedule", body, bearer, idem, ifMatch, ct);

    public Task<GatewayResult> CancelAsync(Guid id, object body, string? bearer, string? idem, string? ifMatch, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/api/v1/appointments/{id}/cancel", body, bearer, idem, ifMatch, ct);

    private async Task<GatewayResult> SendAsync(HttpMethod method, string path, object? body,
        string? bearer, string? idem, string? ifMatch, CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient("emr");
            using var req = new HttpRequestMessage(method, path);
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            if (!string.IsNullOrWhiteSpace(idem)) req.Headers.TryAddWithoutValidation("Idempotency-Key", idem);
            if (!string.IsNullOrWhiteSpace(ifMatch)) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (body is not null)
                req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            Guid? apptId = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                if (doc.RootElement.TryGetProperty("appointmentId", out var el) && el.TryGetGuid(out var g)) apptId = g;
            }
            catch (JsonException) { /* non-JSON body (e.g. problem+json without id) — leave null */ }
            return new GatewayResult((int)resp.StatusCode, text, apptId, resp.Content.Headers.ContentType?.MediaType);
        }
        catch (HttpRequestException) { return new GatewayResult(502, null, null); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new GatewayResult(504, null, null); }
    }
}
