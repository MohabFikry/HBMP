using Mersal.Eligibility.Domain;

namespace Mersal.Eligibility.Api;

/// <summary>POST /eligibility/check request (17-api-specifications §5).</summary>
public sealed record EligibilityCheckRequest(
    Guid BeneficiaryId,
    string BenefitCategory,
    string? ServiceCode,
    bool? ServiceRequiresPreAuth);

/// <summary>Denormalized limit state for the response.</summary>
public sealed record LimitStateResponse(string LimitType, decimal LimitValue, decimal ConsumedValue, decimal Remaining);

/// <summary>POST /eligibility/check response (17-api-specifications §5).</summary>
public sealed record EligibilityCheckResponse(
    string Decision,
    Guid? CoverageId,
    IReadOnlyList<string> Reasons,
    LimitStateResponse? LimitState,
    DateTimeOffset SnapshotExpiresAt,
    bool FromCache)
{
    public static EligibilityCheckResponse From(EligibilityResult r, DateTimeOffset expires, bool fromCache) => new(
        r.Decision.ToString(),
        r.CoverageId,
        r.Reasons,
        r.LimitState is null ? null
            : new LimitStateResponse(r.LimitState.LimitType.ToString(), r.LimitState.LimitValue, r.LimitState.ConsumedValue, r.LimitState.Remaining),
        expires,
        fromCache);
}
