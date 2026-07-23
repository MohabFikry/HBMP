using Mersal.Provider.Domain;

namespace Mersal.Provider.Api;

// Request/response DTOs. agreed_price is T2 financial — response projections mask it unless the caller
// holds the provider:finance scope (see PriceView in Program.cs).

public sealed record CreateProvider(string ProviderCode, string LegalName, string ProviderType);

public sealed record CreateLocation(string Name, string? Governorate, string? Address, decimal? GeoLat, decimal? GeoLng, bool IsPrimary);

public sealed record CreateContract(string ContractNo, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record AddServiceLine(string ServiceType, string CodeSystem, string Code, decimal AgreedPrice, string? CurrencyCode);

public sealed record AddCredential(string CredentialType, DateOnly? ValidFrom, DateOnly? ValidTo, Guid? DocumentId, bool IsMandatory);

public sealed record ProviderView(
    Guid ProviderId, string ProviderCode, string LegalName, string ProviderType, string ProviderTypeLabel,
    string Status, string OnboardingState);

public sealed record CapabilityView(string ServiceType, string CodeSystem, string Code, decimal? AgreedPrice, string? CurrencyCode);
