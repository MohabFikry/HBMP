namespace Mersal.Policy.Domain;

// Phase 18.A1 (audit R2 X1) — the accumulator side of the benefit spine.
//
// coverage_limit.consumed_value is the AUTHORITATIVE accumulator (15-database-erd §5, 22 §3.3) and is
// moved by exactly one path: the consume/dispense fulfillment events (FR-INV-006). Claims read it and
// NEVER write it (36 §2.3, FR-CLM-057). Before this file existed nothing incremented it at all, so
// remaining always equalled the full limit and no member could ever exhaust a benefit.

/// <summary>Which way a fulfillment event moves the accumulator. A void/compensating fulfillment
/// decrements symmetrically so the accumulator always mirrors the append-only fulfillment ledger.</summary>
public enum ConsumptionDirection { Applied, Reversed }

/// <summary>What the applier did with one instruction. Everything except <see cref="Applied"/> and
/// <see cref="Reversed"/> is a no-move outcome that must still be recorded and audited — a silently
/// skipped accumulation is exactly the defect this file closes.</summary>
public enum ConsumptionOutcome
{
    /// <summary>consumed_value moved up.</summary>
    Applied,
    /// <summary>consumed_value moved down (void/compensation).</summary>
    Reversed,
    /// <summary>This source_ref was already applied — no second move (at-least-once delivery).</summary>
    Replayed,
    /// <summary>The event's service has no benefit category in the canonical vocabulary (22 §11).</summary>
    NoBenefitCategory,
    /// <summary>No active, in-effect coverage for (beneficiary, category) on the service date.</summary>
    NoCoverage,
    /// <summary>The coverage exists but carries no accumulating limit — nothing to move.</summary>
    NoAccumulatingLimit,
    /// <summary>A reversal larger than what was consumed — refused rather than clamped to a false 0.</summary>
    WouldGoNegative,
}

/// <summary>One accumulator move requested by a fulfillment event, resolved to pure values at the
/// boundary. <paramref name="SourceRef"/> is the dedupe key: unique per (fulfillment line, direction),
/// so a redelivered event and a retried void are both exact no-ops.</summary>
public sealed record ConsumptionInstruction(
    Guid EventId,
    string EventType,
    string TenantId,
    Guid BeneficiaryId,
    string? BenefitCategory,
    string SourceRef,
    decimal Quantity,
    ConsumptionDirection Direction,
    DateOnly OnDate,
    /// <summary>19.4 — WHERE the care was delivered, so utilization can resolve the network tier in force on
    /// <paramref name="OnDate"/> at report time. Optional: an event that does not carry it still accumulates
    /// (the benefit was used either way) and reports in the explicit unattributed bucket.</summary>
    Guid? ProviderId = null,
    Guid? ProviderLocationId = null);

/// <summary>The result of applying one instruction, including which limits actually moved.</summary>
public sealed record ConsumptionResult(ConsumptionOutcome Outcome, Guid? CoverageId, IReadOnlyList<Guid> MovedLimits)
{
    public static ConsumptionResult None(ConsumptionOutcome outcome, Guid? coverageId = null) => new(outcome, coverageId, []);
}

/// <summary>Append-only ledger row: one per accumulator move AND one per deliberate no-move, so the
/// reconciliation trail between the fulfillment ledger and the accumulator is complete. Never updated,
/// never deleted. <see cref="SourceRef"/> is UNIQUE — it is the duplicate-proof anchor.</summary>
public sealed class BenefitConsumptionRecord
{
    public Guid ConsumptionId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid EventId { get; set; }
    public string EventType { get; set; } = default!;
    public string SourceRef { get; set; } = default!;
    public Guid BeneficiaryId { get; set; }
    public string? BenefitCategory { get; set; }
    public Guid? CoverageId { get; set; }
    public decimal Quantity { get; set; }
    public ConsumptionDirection Direction { get; set; }
    public ConsumptionOutcome Outcome { get; set; }
    public int MovedLimits { get; set; }
    public DateTimeOffset AppliedAt { get; set; }

    /// <summary>19.4 — the provider whose tier this movement is attributed to, and the date the tier is
    /// resolved AT. <see cref="AppliedAt"/> is when the accumulator moved, which lags the care by however
    /// long the broker and any retry took; resolving a tier at that instant would price February's care
    /// against March's network.</summary>
    public Guid? ProviderId { get; set; }
    public Guid? ProviderLocationId { get; set; }
    public DateOnly? ServiceDate { get; set; }
}

/// <summary>The background consumer's dedupe ledger (at-least-once delivery). Transport-level, no
/// tenant data — deliberately outside RLS, matching eligibility.processed_event.</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Pure accumulation rules — no I/O, so the boundary and the tests exercise the same decisions.</summary>
public static class BenefitAccumulation
{
    /// <summary>The canonical benefit-category vocabulary (22-data-dictionary §11 / 15-database-erd §5).
    /// Deliberately closed: an order type outside it must surface as <see cref="ConsumptionOutcome.NoBenefitCategory"/>
    /// rather than be quietly mapped onto an unrelated category.</summary>
    public static readonly IReadOnlyList<string> Categories = ["LAB", "IMAGING", "PHARMACY", "CONSULT", "REFERRAL"];

    /// <summary>True for limit kinds that carry a RUNNING TOTAL across fulfillments. PerEncounter is a
    /// per-encounter ceiling, not a cumulative accumulator — accumulating it would make a member
    /// permanently ineligible after their first encounter, so it is deliberately excluded.</summary>
    public static bool Accumulates(LimitType limitType) =>
        limitType is LimitType.Annual or LimitType.Lifetime or LimitType.Count;

    /// <summary>Signed movement applied to consumed_value.</summary>
    public static decimal SignedDelta(ConsumptionDirection direction, decimal quantity) =>
        direction == ConsumptionDirection.Reversed ? -quantity : quantity;

    /// <summary>Coverage applicability for an accumulator move: active, not soft-deleted, and in effect
    /// on the service date. Mirrors <c>EligibilityEngine</c> step (2) so the accumulator and the decision
    /// engine can never disagree about which coverage a service lands on.</summary>
    public static bool IsApplicable(CoverageStatus status, bool isDeleted, DateOnly effectiveFrom, DateOnly? effectiveTo, DateOnly onDate) =>
        !isDeleted
        && status == CoverageStatus.Active
        && effectiveFrom <= onDate
        && (effectiveTo is null || effectiveTo >= onDate);

    /// <summary>Dedupe key for one fulfillment line in one direction.</summary>
    public static string SourceRef(string eventType, Guid lineId, string idempotencyKey, ConsumptionDirection direction) =>
        $"{eventType}|{lineId}|{idempotencyKey}|{direction}";
}
