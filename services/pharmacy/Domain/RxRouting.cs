using Mersal.Amounts;

namespace Mersal.Pharmacy.Domain;

/// <summary>Config-driven prescription routing (US-033): a prescription containing an expensive/gated drug stays
/// Submitted awaiting an approval decision (dispensable only once Approved); otherwise it auto-approves per
/// policy. Config stand-in for masterdata/policy pricing until the approvals service (phase 7) owns it.</summary>
public sealed class RxRoutingOptions
{
    /// <summary>Drug ids that always require approval before dispensing.</summary>
    public HashSet<Guid> GatedDrugIds { get; set; } = [];

    /// <summary>Per-drug estimated unit cost (config stand-in). Missing = 0.</summary>
    public Dictionary<Guid, decimal> UnitCosts { get; set; } = [];

    /// <summary>Estimated total cost at/above which the prescription routes to approval. 0 disables it.</summary>
    public decimal HighCostThreshold { get; set; }
}

public sealed record RxRoutingDecision(bool RequiresApproval, string Reason);

public static class RxRoutingPolicy
{
    public static RxRoutingDecision Evaluate(Prescription rx, RxRoutingOptions opts)
    {
        var gated = rx.Lines.FirstOrDefault(l => opts.GatedDrugIds.Contains(l.DrugId));
        if (gated is not null)
            return new RxRoutingDecision(true, $"gated-drug:{gated.DrugId}");

        if (opts.HighCostThreshold > 0)
        {
            /*
             * THE ESTIMATE IS MONEY, SO IT IS ADDED UP AS MONEY (ADR-0043).
             *
             * This decides whether a prescription needs a human to approve it, by comparing a sum against a
             * threshold. As bare decimals the sum was unrounded — a per-line product carrying four or five
             * decimal places, accumulated across the lines — and then compared to a threshold somebody typed
             * as `5000`. A prescription landing within a fraction of a piastre of the line could fall either
             * way depending on how many lines it happened to have. Money rounds each product once, at the
             * platform's settlement scale, so the estimate is the number a person would arrive at.
             */
            var currency = Currency.Egp;   // pharmacy prices in the platform currency; see ADR-0043
            var estimate = rx.Lines.Aggregate(
                Money.Zero(currency),
                (running, l) => running
                    + new Money(opts.UnitCosts.TryGetValue(l.DrugId, out var c) ? c : 0m, currency)
                      * l.QuantityPrescribed);
            var threshold = new Money(opts.HighCostThreshold, currency);
            if (estimate >= threshold)
                return new RxRoutingDecision(true, $"high-cost:{estimate.Amount}>={threshold.Amount}");
        }

        return new RxRoutingDecision(false, "auto-approve");
    }
}
