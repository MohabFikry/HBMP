namespace Mersal.Claims.Domain;

/// <summary>Builds the settlement-advice projection from a batch's member lines + their net adjustment deltas (36 §8).
/// Pure and deterministic — the same inputs always render the same document (so the content hash is stable). Totals
/// follow the batch rollup: claimed → priced → approved → adjustments → net payable. Void lines are excluded.</summary>
public static class SettlementBuilder
{
    public static SettlementProjection Build(
        string batchNo, Guid? payeeProviderId, Guid? providerLocationId, DateOnly periodFrom, DateOnly periodTo,
        string generatedBy, DateTimeOffset generatedAt, int version,
        IEnumerable<(string ClaimNo, ClaimLine Line, decimal AdjustedDelta)> lines)
    {
        var rows = new List<SettlementLineRow>();
        decimal claimed = 0, priced = 0, approved = 0, adjusted = 0, denied = 0;
        foreach (var (claimNo, l, delta) in lines.OrderBy(x => x.ClaimNo).ThenBy(x => x.Line.Code))
        {
            if (l.Status == ClaimLineStatus.Void) continue;
            rows.Add(new SettlementLineRow(claimNo, l.CodeSystem.ToString(), l.Code, l.Quantity, l.BilledAmount,
                l.ContractPrice, l.AllowedAmount, delta, l.Status.ToString(), l.ReasonCodes));
            claimed += l.BilledAmount;
            priced += l.ContractPrice ?? 0m;
            switch (l.Status)
            {
                case ClaimLineStatus.Approved:
                case ClaimLineStatus.PartiallyApproved:
                case ClaimLineStatus.Adjusted:
                    approved += l.AllowedAmount ?? 0m;
                    break;
                case ClaimLineStatus.Denied:
                    denied += l.BilledAmount;
                    break;
            }
            adjusted += delta;
        }
        return new SettlementProjection(batchNo, payeeProviderId, providerLocationId, periodFrom, periodTo,
            generatedBy, generatedAt, version, rows, claimed, priced, approved, adjusted, denied, approved + adjusted);
    }
}
