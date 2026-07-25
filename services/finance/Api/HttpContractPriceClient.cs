using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Finance.Infrastructure;

namespace Mersal.Finance.Api;

/// <summary>Reads the in-effect agreed price book from provider-service (<c>provider_contract</c> /
/// <c>contract_service_line</c>, 22 §5.3) with the caller's bearer token — finance READS these prices, it never
/// duplicates or mutates contract data. Fail-soft: if the provider price API can't be reached the book is empty and
/// settlement lines fall back to the observed unit cost (never a fabricated price).</summary>
public sealed class HttpContractPriceClient(HttpClient http) : IContractPriceProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ContractPriceBook?> GetPriceBookAsync(Guid providerId, DateOnly asOf, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/providers/{providerId}/contract-prices?asOf={asOf:yyyy-MM-dd}");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearerToken["Bearer ".Length..] : bearerToken;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return ContractPriceBook.Empty();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync<PriceBookDto>(stream, Json, ct);
            if (dto is null) return ContractPriceBook.Empty();
            var prices = (dto.Lines ?? [])
                .GroupBy(l => l.ServiceCode ?? "", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().AgreedPrice, StringComparer.Ordinal);
            return new ContractPriceBook(dto.ContractId, dto.CurrencyCode ?? "EGP", prices);
        }
        catch (HttpRequestException) { return ContractPriceBook.Empty(); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return ContractPriceBook.Empty(); }
    }

    private sealed record PriceBookDto(Guid ContractId, string? CurrencyCode, List<PriceLineDto>? Lines);
    private sealed record PriceLineDto(string? ServiceCode, decimal AgreedPrice);
}
