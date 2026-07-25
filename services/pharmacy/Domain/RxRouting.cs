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
            var estimate = rx.Lines.Sum(l =>
                (opts.UnitCosts.TryGetValue(l.DrugId, out var c) ? c : 0m) * l.QuantityPrescribed);
            if (estimate >= opts.HighCostThreshold)
                return new RxRoutingDecision(true, $"high-cost:{estimate}>={opts.HighCostThreshold}");
        }

        return new RxRoutingDecision(false, "auto-approve");
    }
}
