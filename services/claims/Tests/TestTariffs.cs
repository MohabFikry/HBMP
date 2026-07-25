using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;

namespace Mersal.Claims.Tests;

/// <summary>Deterministic in-memory tariff provider for tests: returns a fixed price, or null (⇒ NO_TARIFF) when
/// constructed with none. Avoids any HTTP dependency on provider-service.</summary>
public sealed class FixedTariff(decimal? price) : IContractTariffProvider
{
    public Task<decimal?> ResolveAsync(Guid providerId, ClaimCodeSystem codeSystem, string code, DateOnly serviceDate,
        string? bearerToken, CancellationToken ct = default) => Task.FromResult(price);
}
