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
///
/// <para><b>29.1 — the benefit category stays <c>IMAGING</c> while the order type becomes Radiology</b>
/// (design 45 §1, ADR-0029). The two vocabularies are deliberately not the same one. <c>IMAGING</c> is a
/// COVERAGE category: it is a CHECK constraint in policy 0001 and eligibility 0006, a seeded
/// <c>benefit_category</c> row, a value inside every plan's coverage limits, and the key claims adjudication
/// and interop map against. Design 45 §1 renames the role, the scopes, the provider type, the order type, the
/// events, the portal base and the UI strings — it does not name the coverage vocabulary, and renaming it
/// would rewrite live benefit accumulators to chase a label. The mapping is the seam, and this is it.</para>
/// </summary>
public static class BenefitCategoryMap
{
    public static string? ForOrderType(OrderType orderType) => OrderTypes.Canonical(orderType) switch
    {
        OrderType.Lab => "LAB",
        OrderType.Radiology => "IMAGING",
        _ => null,
    };
}
