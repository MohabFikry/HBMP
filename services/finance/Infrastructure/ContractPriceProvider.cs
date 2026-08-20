namespace Mersal.Finance.Infrastructure;

/// <summary>The agreed price for a provider's service code, READ from the provider-service
/// <c>provider_contract</c> / <c>contract_service_line</c> (22 §5.3). Finance READS these prices — it never
/// duplicates or mutates contract data. The HTTP implementation lives in the Api layer; tests inject a fake.</summary>
public interface IContractPriceProvider
{
    /// <summary>The in-effect agreed price book for a provider on a date: service_code → (unit price, currency,
    /// contract id). A missing code means "no agreed price" → the settlement line falls back to the LOWEST
    /// observed unit cost and is marked ObservedFloor, so a reviewer can see it had no tariff.</summary>
    Task<ContractPriceBook?> GetPriceBookAsync(Guid providerId, DateOnly asOf, string? bearerToken, CancellationToken ct = default);
}

/// <summary>An in-effect price book: the contract id + a service_code → agreed unit price map.</summary>
public sealed record ContractPriceBook(Guid ContractId, string CurrencyCode, IReadOnlyDictionary<string, decimal> Prices)
{
    public bool TryPrice(string serviceCode, out decimal price) => Prices.TryGetValue(serviceCode, out price);

    public static ContractPriceBook Empty(Guid contractId = default) =>
        new(contractId, "EGP", new Dictionary<string, decimal>(StringComparer.Ordinal));
}
