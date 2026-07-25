using Mersal.Claims.Domain;

namespace Mersal.Claims.Api;

/// <summary>Create-batch request (10b.2). Only the fields a mode needs are read: DateRange uses the service-date
/// window + payee; ProviderBranch adds providerLocationId; ProviderGroup uses providerGroupId; Manual uses claimIds.</summary>
public sealed record CreateBatchRequest(
    BatchType BatchType,
    BatchSelectionMode SelectionMode,
    Guid? PayeeProviderId,
    Guid? ProviderLocationId,
    Guid? ProviderGroupId,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateOnly? ServiceDateFrom,
    DateOnly? ServiceDateTo,
    Guid[]? ClaimIds);

public sealed record RemoveClaimRequest(string? Reason);
public sealed record CancelBatchRequest(string? Reason);

/// <summary>Min-necessary batch projection — payee, period, status, rollups and membership. No clinical fields.</summary>
public sealed record BatchView(
    Guid BatchId,
    string BatchNo,
    string BatchType,
    string SelectionMode,
    Guid? PayeeProviderId,
    Guid? ProviderLocationId,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    string Status,
    decimal TotalClaimed,
    decimal TotalPriced,
    decimal TotalApproved,
    decimal TotalAdjusted,
    decimal TotalDenied,
    decimal NetPayable,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? FrozenAt,
    IReadOnlyList<Guid> ClaimIds)
{
    public static BatchView From(ClaimBatch b) => new(
        b.BatchId, b.BatchNo, b.BatchType.ToString(), b.SelectionMode.ToString(), b.PayeeProviderId,
        b.ProviderLocationId, b.PeriodFrom, b.PeriodTo, b.Status.ToString(), b.TotalClaimed, b.TotalPriced,
        b.TotalApproved, b.TotalAdjusted, b.TotalDenied, b.NetPayable, b.CreatedAt, b.DecidedAt, b.FrozenAt,
        [.. b.Items.Where(i => i.RemovedAt is null).Select(i => i.ClaimId)]);
}
