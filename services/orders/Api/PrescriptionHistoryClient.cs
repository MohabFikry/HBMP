using System.Net;
using System.Text.Json;
using System.Net.Http.Headers;

namespace Mersal.Orders.Api;

/// <summary>
/// 29.4 — one previous prescription of a medicine, as pharmacy reports it (design 45 §4).
/// </summary>
public sealed record PrescriptionHistoryRow(
    Guid PrescriptionId,
    string RxNo,
    Guid PrescriptionLineId,
    Guid DrugId,
    string? DrugName,
    DateTimeOffset OccurredAt,
    string Status,
    string? PrescriberId,
    Guid? BranchId);

/// <summary>
/// The answer, WITH whether it could be obtained at all.
/// </summary>
/// <param name="Available">
/// False ⇒ pharmacy could not be reached. The distinction is the whole point: design 45 §4 requires three
/// states, and "could not load" rendered as "no previous prescriptions" is the one that makes a clinician
/// re-prescribe something the patient is already taking.
/// </param>
public sealed record PrescriptionHistory(bool Available, IReadOnlyList<PrescriptionHistoryRow> Rows)
{
    public static PrescriptionHistory Unavailable { get; } = new(false, []);
    public static PrescriptionHistory None { get; } = new(true, []);
}

public interface IPrescriptionHistoryClient
{
    Task<PrescriptionHistory> ForBeneficiaryAsync(
        Guid beneficiaryId, string? drugCode, string? bearer, CancellationToken ct = default);
}

/// <summary>
/// 29.4 — the prescription half of the service history, fetched from pharmacy under the CALLER'S token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why orders-service aggregates rather than the client merging two endpoints.</b> Design 45 §4 is
/// explicit that this is ONE endpoint composed server-side, and the reason is the sensitivity gate: two
/// endpoints means two places that decide what a caller may see, and the one that drifts is discovered by
/// someone reading a result they should not have. Composing here keeps the gate in a single place, and
/// pharmacy still applies its own — the bearer is forwarded, so pharmacy answers for the caller and not for
/// orders-service.
/// </para>
/// <para>
/// <b>An outage is an ANSWER.</b> Every failure — transport, timeout, 403, 500, unparseable body — returns
/// <see cref="PrescriptionHistory.Unavailable"/>, never an empty list. The caller renders that as "could not
/// load", which is the third state and the one that must never collapse into the second.
/// </para>
/// </remarks>
public sealed class HttpPrescriptionHistoryClient(IHttpClientFactory factory, ILogger<HttpPrescriptionHistoryClient> log)
    : IPrescriptionHistoryClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A history modal must not hold the encounter open. A timeout here is an answer: unavailable.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<PrescriptionHistory> ForBeneficiaryAsync(
        Guid beneficiaryId, string? drugCode, string? bearer, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var http = factory.CreateClient("pharmacy");
            var q = string.IsNullOrWhiteSpace(drugCode) ? "" : $"?code={Uri.EscapeDataString(drugCode)}";
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/prescriptions/history/{beneficiaryId}{q}");
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? bearer["Bearer ".Length..]
                    : bearer;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await http.SendAsync(req, cts.Token);
            // 404 — this beneficiary has no prescription record. A real answer, and an empty one.
            if (resp.StatusCode == HttpStatusCode.NotFound) return PrescriptionHistory.None;
            if (!resp.IsSuccessStatusCode)
            {
                // A 403 here is NOT "no prescriptions" — it is "this caller may not read them", and
                // rendering it as an empty list is exactly the silent-failure shape this platform refuses.
                log.LogWarning("prescription history unavailable: {Status}", resp.StatusCode);
                return PrescriptionHistory.Unavailable;
            }

            var body = await resp.Content.ReadFromJsonAsync<Dto>(Json, cts.Token);
            return new PrescriptionHistory(true, body?.Items ?? []);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "prescription history could not be fetched");
            return PrescriptionHistory.Unavailable;
        }
    }

    private sealed record Dto(List<PrescriptionHistoryRow>? Items);
}
