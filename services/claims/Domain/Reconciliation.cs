namespace Mersal.Claims.Domain;

/// <summary>The three-view comparison inputs for one reconciliation unit — a (provider, beneficiary, code, period)
/// cell compared across what was DELIVERED (auto-derived from fulfillment), what the provider BILLED, and what was
/// APPROVED (36 §7). Amounts/quantities are the billed-vs-contract and delivered-vs-billed pairs; all min-necessary
/// (codes + amounts, zero clinical fields).</summary>
public readonly record struct ReconInput(
    bool Delivered, bool Billed, bool IsDuplicate,
    decimal? BilledAmount, decimal? ContractPrice, decimal? DeliveredQuantity, decimal? BilledQuantity);

/// <summary>Pure bucket classification for the reconciliation worklist (10b.7, 36 §7). A discrepancy may technically
/// satisfy more than one predicate; this applies the fixed precedence so every row lands in EXACTLY ONE bucket:
/// Duplicate → BilledNotDelivered → DeliveredNotBilled → PriceVariance → QuantityVariance → Matched.</summary>
public static class ReconClassifier
{
    public static ReconBucket Classify(ReconInput x)
    {
        if (x.IsDuplicate) return ReconBucket.Duplicate;
        if (x.Billed && !x.Delivered) return ReconBucket.BilledNotDelivered;
        if (x.Delivered && !x.Billed) return ReconBucket.DeliveredNotBilled;
        // Both delivered and billed: a price disagreement outranks a quantity disagreement.
        if (PriceDiffers(x)) return ReconBucket.PriceVariance;
        if (QuantityDiffers(x)) return ReconBucket.QuantityVariance;
        return ReconBucket.Matched;
    }

    private static bool PriceDiffers(ReconInput x) =>
        x is { Billed: true, Delivered: true, BilledAmount: { } b, ContractPrice: { } c } && b != c;

    private static bool QuantityDiffers(ReconInput x) =>
        x is { Billed: true, Delivered: true, DeliveredQuantity: { } d, BilledQuantity: { } q } && d != q;
}
