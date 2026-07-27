namespace Mersal.Policy.Api;

// CreatePolicy moved to EnrollmentContracts.cs in 19.2 and gained a required PayerId — see the note in Program.cs.
public sealed record CreateCoverage(Guid BeneficiaryId, string BenefitCategoryCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo, CreateLimit[] Limits);
public sealed record CreateLimit(string LimitType, decimal LimitValue, string? CurrencyCode, string? ResetPeriod);
