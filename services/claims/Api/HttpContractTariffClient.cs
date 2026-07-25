using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;

namespace Mersal.Claims.Api;

/// <summary>Resolves the contract tariff by calling provider-service under the caller's bearer token — the price is
/// READ from <c>contract_service_line.agreed_price</c> (36 §5 step 7), never duplicated or mutated in claims. A 404
/// or an empty result ⇒ null ⇒ NO_TARIFF (manual pricing). Any transport error is treated as "no tariff resolved"
/// so a claim line is never mis-priced from a failed call — it routes to manual review, not to a guessed price.</summary>
public sealed class HttpContractTariffClient(HttpClient http) : IContractTariffProvider
{
    public async Task<decimal?> ResolveAsync(Guid providerId, ClaimCodeSystem codeSystem, string code,
        DateOnly serviceDate, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/v1/providers/{providerId}/tariff?codeSystem={codeSystem}&code={Uri.EscapeDataString(code)}&on={serviceDate:yyyy-MM-dd}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                req.Headers.Authorization = AuthenticationHeaderValue.Parse(bearerToken);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Null) return null;
            if (doc.RootElement.TryGetProperty("agreedPrice", out var p) && p.TryGetDecimal(out var price))
                return price;
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // no tariff resolved → manual pricing, never a guessed price
        }
    }
}
