using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Mersal.Eligibility.Api;

/// <summary>
/// Which plan version's terms apply on a given service date.
/// </summary>
/// <remarks>
/// <para>A seam rather than a table, because eligibility does not own the plan layer and must not hold a
/// second copy of its effective-dating rules. policy-service exposes the resolver at
/// <c>GET /plans/{id}/version-at</c> precisely so consumers stop reading "the active version" — the invariant
/// its own comment states: <i>consumers must call this rather than reading the active version</i>.</para>
/// <para><b>It answers null rather than guessing.</b> A plan with no configuration in force on the date, an
/// unreachable policy-service, a refused read — all null, and the caller falls back to the version the
/// coverage was projected from, then to no quote at all. What it never does is return "today's active
/// version" as a stand-in for one it could not resolve: repricing February's care with March's terms is the
/// exact bug the effective-dated layer exists to prevent.</para>
/// </remarks>
public interface IPlanVersionInForce
{
    Task<Guid?> InForceAsync(Guid planId, DateOnly on, string? bearerToken, CancellationToken ct = default);
}

public sealed class HttpPlanVersionInForce(HttpClient http, ILogger<HttpPlanVersionInForce> logger) : IPlanVersionInForce
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A quote must not hold a dispensing counter open. Five seconds, then fall back.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<Guid?> InForceAsync(Guid planId, DateOnly on, string? bearerToken, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/plans/{planId}/version-at?date={on:yyyy-MM-dd}");
            if (!string.IsNullOrWhiteSpace(bearerToken)
                && AuthenticationHeaderValue.TryParse(bearerToken, out var auth))
            {
                req.Headers.Authorization = auth;
            }

            using var resp = await http.SendAsync(req, cts.Token);
            // 409 = the plan had no configuration in force that day, which is an ANSWER; 404 = no such plan.
            // Both mean "no version", and neither is worth a log line on a hot path.
            if (resp.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
            {
                // Anything else is a failure to ASK, which is a different thing and worth seeing: a 403 here
                // is how the whole cost-share path silently reported "not priced" before the gate was
                // narrowed to policy:price-lookup.
                logger.LogWarning(
                    "plan version-at for {PlanId} on {Date} answered {Status}; falling back to the projected version",
                    planId, on, (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<PlanVersionRef>(Json, cts.Token);
            return body?.PlanVersionId;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(e, "plan version-at for {PlanId} could not be read; falling back", planId);
            return null;
        }
    }

    private sealed record PlanVersionRef(Guid PlanVersionId);
}
