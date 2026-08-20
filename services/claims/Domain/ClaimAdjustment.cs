namespace Mersal.Claims.Domain;

/// <summary>An append-only adjustment to a decided claim line (22 §10A.4, 36 §7). NEVER an edit or delete of the
/// original decision — a changed outcome is a NEW signed entry that nets into the batch rollup. Carries the BEFORE and
/// AFTER payable amounts for a hash-chained audit trail. A Recovery/Clawback must reference the original line it
/// recovers against; a Reversal/Void is a compensating entry; a Reallocation is a reversing entry + a new anchored one.</summary>
public sealed class ClaimAdjustment
{
    public Guid AdjustmentId { get; set; }
    public Guid ClaimLineId { get; set; }
    public Guid ClaimId { get; set; }
    public string TenantId { get; set; } = default!;
    public AdjustmentType AdjustmentType { get; set; }
    /// <summary>Signed delta (debit − / credit +); nets into the batch rollup. Never zero.</summary>
    public decimal AmountDelta { get; set; }
    public string ReasonCode { get; set; } = default!;
    public string Rationale { get; set; } = default!;
    /// <summary>Original line recovered against — REQUIRED for Recovery/Clawback.</summary>
    public Guid? RecoversClaimLineId { get; set; }
    public decimal BeforeAmount { get; set; }
    public decimal AfterAmount { get; set; }
    public string AdjustedBy { get; set; } = default!;
    public DateTimeOffset AdjustedAt { get; set; }
    public string CorrelationId { get; set; } = "";
    // dual control: a negative-net adjustment waits for a second, distinct approver.
    public bool PendingSecondApproval { get; set; }
    public Guid? ConfirmsAdjustmentId { get; set; }
    public string? IdempotencyKey { get; set; }

    /// <summary>SHA-256 of the canonical request this key produced (migration 0009). Without it a key reused
    /// across two different amounts returned the first adjustment, so the second correction never happened
    /// and the batch total stayed wrong by the difference. NULL on pre-0009 rows: treated as a match.</summary>
    public string? RequestHash { get; set; }
}

/// <summary>Pure adjustment rules (36 §7). Validates the mandatory fields and reference requirements, and defines the
/// SIGN each adjustment type must carry, so the netting arithmetic and the recovery-reference rule are unit-tested
/// without a database.</summary>
public static class AdjustmentRules
{
    /// <summary>The sign an adjustment type must carry: −1 = must reduce payable (debit), +1 = must increase (credit),
    /// 0 = either (a correction/reallocation may move up or down). Enforced so a "Writeoff" can never quietly pay MORE.</summary>
    public static int RequiredSign(AdjustmentType type) => type switch
    {
        AdjustmentType.Deduction => -1,
        AdjustmentType.Recovery => -1,
        AdjustmentType.Clawback => -1,
        AdjustmentType.Writeoff => -1,
        AdjustmentType.Reversal => -1,
        AdjustmentType.Void => -1,
        _ => 0, // PriceCorrection, QuantityCorrection, Reallocation — signed either way
    };

    /// <summary>Returns null when valid, else a coded error token (422). A delta must be non-zero and its sign must
    /// match the type; reason code + non-blank rationale are mandatory; Recovery/Clawback require the original line.</summary>
    public static string? Validate(
        AdjustmentType type, decimal amountDelta, string? reasonCode, string? rationale, Guid? recoversClaimLineId)
    {
        if (amountDelta == 0m) return "amount-delta-required";
        var sign = RequiredSign(type);
        if (sign == -1 && amountDelta > 0m) return "sign-must-be-negative";
        if (sign == 1 && amountDelta < 0m) return "sign-must-be-positive";
        if (string.IsNullOrWhiteSpace(reasonCode) || !ReasonCodes.IsKnown(reasonCode)) return "reason-code-required";
        if (string.IsNullOrWhiteSpace(rationale)) return "rationale-required";
        if (type is AdjustmentType.Recovery or AdjustmentType.Clawback && recoversClaimLineId is null)
            return "recovery-reference-required";
        return null;
    }

    /// <summary>The line status an adjustment drives the line to: a Reversal/Void voids the line; every other type
    /// marks it Adjusted (the allowed amount is re-netted).</summary>
    public static ClaimLineStatus ResultingStatus(AdjustmentType type) =>
        type is AdjustmentType.Reversal or AdjustmentType.Void ? ClaimLineStatus.Void : ClaimLineStatus.Adjusted;
}
