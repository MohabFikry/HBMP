namespace Mersal.Claims.Domain;

// ---- KPI input facts (de-identified, aggregate-only — NO clinical fields, no direct identifiers beyond provider) ----

/// <summary>A decided-claim fact for KPI aggregation (36 §11). Codes + amounts + timing only.</summary>
public sealed record DecidedClaimFact(
    Guid? ProviderId, ClaimStatus Status, DateTimeOffset? SubmittedAt, DateTimeOffset? DecidedAt,
    decimal ApprovedAmount, decimal BilledAmount, IReadOnlyList<string> DenialReasonCodes);

public sealed record AdjustmentFact(AdjustmentType Type, decimal AmountDelta);
public sealed record ReimbursementFact(ReimbursementMatchMethod Method);
public sealed record UnbilledFact(Guid? ProviderId, decimal Amount);

public sealed record ReasonCount(string ReasonCode, int Count);
public sealed record AdjustmentValue(string Type, decimal TotalAbsValue);
public sealed record ProviderVariance(Guid ProviderId, decimal Variance);

/// <summary>The claims KPI aggregate (36 §11). AGGREGATE-ONLY, no clinical fields, no direct identifiers except the
/// provider (needed for the variance league). Consumed by reporting-service (phase 8); dashboards live there.</summary>
public sealed record ClaimsKpi(
    double AverageTatHours, decimal ApprovalRate, decimal DenialRate,
    IReadOnlyList<ReasonCount> TopDenialReasons, IReadOnlyList<AdjustmentValue> AdjustmentValueByType,
    IReadOnlyList<ProviderVariance> ProviderVarianceLeague, decimal OcrAutoMatchRate,
    int AgedUnbilledCount, decimal AgedUnbilledValue, decimal RecoveryOutstanding);

/// <summary>Pure claims-KPI computation (36 §11) — deterministic over a fixture so each metric is unit-tested in
/// isolation. Rates are 0–1; TAT is average submission→decision hours over decided claims. Nothing here reads or
/// emits a clinical field.</summary>
public static class ClaimsKpiCalculator
{
    private static readonly ClaimStatus[] ApprovedStates =
        [ClaimStatus.Approved, ClaimStatus.PartiallyApproved];

    public static ClaimsKpi Compute(
        IReadOnlyList<DecidedClaimFact> claims, IReadOnlyList<AdjustmentFact> adjustments,
        IReadOnlyList<ReimbursementFact> reimbursements, IReadOnlyList<UnbilledFact> unbilled, int topReasons = 5)
    {
        var decided = claims.Where(c => c.DecidedAt is not null).ToList();

        var tat = decided
            .Where(c => c.SubmittedAt is not null)
            .Select(c => (c.DecidedAt!.Value - c.SubmittedAt!.Value).TotalHours)
            .DefaultIfEmpty(0).Average();

        var approved = decided.Count(c => ApprovedStates.Contains(c.Status));
        var denied = decided.Count(c => c.Status == ClaimStatus.Denied);
        var approvalRate = Rate(approved, decided.Count);
        var denialRate = Rate(denied, decided.Count);

        var topDenial = decided
            .SelectMany(c => c.DenialReasonCodes)
            .GroupBy(r => r)
            .Select(g => new ReasonCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.ReasonCode)
            .Take(topReasons).ToList();

        var adjByType = adjustments
            .GroupBy(a => a.Type)
            .Select(g => new AdjustmentValue(g.Key.ToString(), g.Sum(x => Math.Abs(x.AmountDelta))))
            .OrderByDescending(x => x.TotalAbsValue).ThenBy(x => x.Type).ToList();

        var providerVariance = decided
            .Where(c => c.ProviderId is not null)
            .GroupBy(c => c.ProviderId!.Value)
            .Select(g => new ProviderVariance(g.Key, g.Sum(c => c.BilledAmount - c.ApprovedAmount)))
            .OrderByDescending(x => x.Variance).ToList();

        var ocrAuto = reimbursements.Count(r => r.Method == ReimbursementMatchMethod.AutoOcr);
        var ocrRate = Rate(ocrAuto, reimbursements.Count);

        var recoveryOutstanding = adjustments
            .Where(a => a.Type is AdjustmentType.Recovery or AdjustmentType.Clawback)
            .Sum(a => Math.Abs(a.AmountDelta));

        return new ClaimsKpi(
            Math.Round(tat, 2), approvalRate, denialRate, topDenial, adjByType, providerVariance, ocrRate,
            unbilled.Count, unbilled.Sum(u => u.Amount), recoveryOutstanding);
    }

    private static decimal Rate(int numerator, int total) =>
        total == 0 ? 0m : Math.Round((decimal)numerator / total, 4);
}
