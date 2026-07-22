namespace Mersal.Policy.Api;

public sealed record CreatePolicy(string PolicyNo, string? Sponsor, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
public sealed record CreateCoverage(Guid BeneficiaryId, string BenefitCategoryCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo, CreateLimit[] Limits);
public sealed record CreateLimit(string LimitType, decimal LimitValue, string? CurrencyCode, string? ResetPeriod);
