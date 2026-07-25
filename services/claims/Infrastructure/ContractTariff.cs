using Mersal.Claims.Domain;

namespace Mersal.Claims.Infrastructure;

/// <summary>Resolves the performing provider's agreed tariff for a service code on a service date, by calling
/// provider-service for <c>contract_service_line.agreed_price</c> (36 §5 step 7). Returns null when NO tariff exists
/// for the code/date — the caller then records NO_TARIFF and routes the line to manual pricing. A price is NEVER
/// defaulted, estimated, averaged, or carried over from another provider/date. The HTTP implementation lives in the
/// Api layer; tests inject a fake.</summary>
public interface IContractTariffProvider
{
    Task<decimal?> ResolveAsync(Guid providerId, ClaimCodeSystem codeSystem, string code, DateOnly serviceDate,
        string? bearerToken, CancellationToken ct = default);
}

/// <summary>Fallback that resolves no tariff — every line becomes NO_TARIFF / manual pricing. Used only where a real
/// provider client is not wired (keeps the service booting without inventing prices).</summary>
public sealed class NoTariffProvider : IContractTariffProvider
{
    public Task<decimal?> ResolveAsync(Guid providerId, ClaimCodeSystem codeSystem, string code, DateOnly serviceDate,
        string? bearerToken, CancellationToken ct = default) => Task.FromResult<decimal?>(null);
}
