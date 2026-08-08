using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Inventory.Domain;

namespace Mersal.Inventory.Api;

/// <summary>
/// D5 enforcement, transport half — asks masterdata-service whether a proposed catalogue item is a medicine
/// (<c>GET /api/v1/drugs/classify</c>). The seam and the reasoning live on
/// <see cref="IMedicinesDirectory"/>; this class is only how the question travels.
///
/// <para>Same shape as <see cref="HttpBranchDirectory"/> next door, including forwarding the caller's bearer
/// so masterdata authorizes the same principal rather than a service identity with wider reach.</para>
///
/// <para><b>Not cached.</b> <c>HttpBranchDirectory</c> caches for 60s because it is consulted on every
/// request; this is consulted only when someone creates a catalogue item, which happens rarely. A cache here
/// would buy nothing and would mean a drug added to the master could still be admitted as clinic stock for
/// the length of the TTL.</para>
/// </summary>
public sealed class HttpMedicinesDirectory(HttpClient http, IHttpContextAccessor ctx, ILogger<HttpMedicinesDirectory> log)
    : IMedicinesDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MedicineCheck> ClassifyAsync(string sku, string nameEn, string? nameAr, CancellationToken ct = default)
    {
        var url = "/api/v1/drugs/classify"
            + $"?code={Uri.EscapeDataString(sku ?? "")}"
            + $"&name={Uri.EscapeDataString(nameEn ?? "")}"
            + $"&nameAr={Uri.EscapeDataString(nameAr ?? "")}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var bearer = ctx.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var dto = await resp.Content.ReadFromJsonAsync<ClassifyDto>(Json, ct);

            // A 200 that deserialized to nothing is an UNANSWERED question, not a "no". Treating it as a no
            // would turn every contract drift into a silently open gate — the exact failure this seam exists
            // to prevent, arriving through the one path that looks like success.
            if (dto is null) return MedicineCheck.Unreachable;

            return dto.Matched
                ? new MedicineCheck(MedicineVerdict.IsAMedicine, dto.DrugCode, dto.Name, dto.AtcCode, dto.IsVaccine)
                : MedicineCheck.NotAMedicine;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Logged at WARNING, with no item name: a refusal the operator cannot explain is a support
            // ticket, and "masterdata was down" is the whole explanation.
            log.LogWarning(ex, "medicines directory unreachable — clinic-stock item creation is refused while it is down");
            return MedicineCheck.Unreachable;
        }
    }

    private sealed record ClassifyDto(bool Matched, string? DrugCode, string? Name, string? AtcCode, bool IsVaccine);
}
