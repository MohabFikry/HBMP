using Mersal.Claims.Domain;

namespace Mersal.Claims.Api;

/// <summary>Min-necessary claim projection (10b.1). A server-side allow-list DTO: it carries service CODES, AMOUNTS,
/// linkage ids and statuses only. There is no diagnosis / EMR note / result value field here — nor any such column
/// in the schema to source one from (22 §10A). This is code, not a comment: the clinical fields are absent from the
/// payload, not merely null.</summary>
public sealed record ClaimView(
    Guid ClaimId,
    string ClaimNo,
    string Origin,
    Guid BeneficiaryId,
    Guid? ProviderId,
    Guid? ProviderLocationId,
    Guid? AuthorizationId,
    Guid? BatchId,
    DateOnly ServiceDateFrom,
    DateOnly? ServiceDateTo,
    string CurrencyCode,
    decimal ClaimedAmount,
    decimal? PricedAmount,
    decimal? ApprovedAmount,
    decimal? AdjustedAmount,
    decimal? NetPayable,
    string Status,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ClaimLineView> Lines)
{
    public static ClaimView From(Claim c) => new(
        c.ClaimId, c.ClaimNo, c.Origin.ToString(), c.BeneficiaryId, c.ProviderId, c.ProviderLocationId,
        c.AuthorizationId, c.BatchId, c.ServiceDateFrom, c.ServiceDateTo, c.CurrencyCode, c.ClaimedAmount,
        c.PricedAmount, c.ApprovedAmount, c.AdjustedAmount, c.NetPayable, c.Status.ToString(),
        c.SubmittedAt, c.DecidedAt, c.CreatedAt,
        [.. c.Lines.OrderBy(l => l.Code).Select(ClaimLineView.From)]);
}

/// <summary>Min-necessary claim-line projection — codes, quantities, amounts, adjudication output, and linkage. No
/// clinical content: result/report EXISTENCE + references belong to the 10b.4 worklist, never values.</summary>
public sealed record ClaimLineView(
    Guid ClaimLineId,
    Guid? FulfillmentRef,
    string FulfillmentType,
    string CodeSystem,
    string Code,
    string? Description,
    decimal Quantity,
    decimal BilledAmount,
    decimal? ContractPrice,
    decimal? AllowedAmount,
    decimal? MemberShare,
    string Status,
    string? SystemRecommendation,
    IReadOnlyList<string> ReasonCodes,
    Guid? AuthorizationId,
    string? RuleVersion)
{
    public static ClaimLineView From(ClaimLine l) => new(
        l.ClaimLineId, l.FulfillmentRef, l.FulfillmentType.ToString(), l.CodeSystem.ToString(), l.Code,
        l.Description, l.Quantity, l.BilledAmount, l.ContractPrice, l.AllowedAmount, l.MemberShare,
        l.Status.ToString(), l.SystemRecommendation?.ToString(), l.ReasonCodes, l.AuthorizationId, l.RuleVersion);
}

/// <summary>Auto-derive intake seam payload (10b.1). Built at the boundary from an <c>OrderLinesConsumed</c> /
/// <c>RxLinesDispensed</c> event — carries billing fields only; any clinical field on the source event is dropped
/// here and never reaches the claims schema. Mirrors the finance <c>/projections</c> seam pending the fanout bus.</summary>
public sealed record ClaimIntakeRequest(
    Guid EventId,
    string EventType,
    string TenantId,
    Guid FulfillmentRef,
    FulfillmentType FulfillmentType,
    Guid BeneficiaryId,
    Guid ProviderId,
    Guid? ProviderLocationId,
    Guid? AuthorizationId,
    ClaimCodeSystem CodeSystem,
    string Code,
    string? Description,
    decimal Quantity,
    decimal BilledAmount,
    DateOnly ServiceDate,
    string? CurrencyCode,
    DateTimeOffset OccurredAt);
