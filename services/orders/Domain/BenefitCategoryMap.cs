namespace Mersal.Orders.Domain;

/// <summary>
/// Phase 18.A1 — the benefit category a consumed order line lands on, so policy-service can move the
/// right <c>coverage_limit.consumed_value</c> accumulator (FR-INV-006).
///
/// The vocabulary is CLOSED to the canonical set in 22-data-dictionary §11 / 15-database-erd §5:
/// LAB, IMAGING, PHARMACY, CONSULT, REFERRAL. <see cref="OrderType.Procedure"/> has no counterpart in
/// that set, so it maps to <c>null</c> — the event says "no category" and the accumulator records a
/// visible, audited no-move rather than being quietly charged against an unrelated benefit. Flagged as a
/// spec gap: either 22 §11 gains a PROCEDURE category or procedures are declared out of benefit scope.
/// </summary>
public static class BenefitCategoryMap
{
    public static string? ForOrderType(OrderType orderType) => orderType switch
    {
        OrderType.Lab => "LAB",
        OrderType.Imaging => "IMAGING",
        _ => null,
    };
}
