namespace Mersal.Inventory.Domain;

/// <summary>
/// The stock rules, pure and in Domain so both the API and the tests answer them identically (design 42 §5).
/// </summary>
public static class StockRules
{
    public const string BatchExpiredProblemType = "urn:hbmp:batch-expired";
    public const string InsufficientStockProblemType = "urn:hbmp:insufficient-stock";
    public const string BatchRequiredProblemType = "urn:hbmp:batch-required";
    public const string ReasonRequiredProblemType = "urn:hbmp:reason-required";

    /// <summary>Kinds whose sign is fixed. Adjustment and Count are absent because both record a VARIANCE and
    /// a variance goes in both directions.</summary>
    public static int? FixedSign(MovementKind kind) => kind switch
    {
        MovementKind.Receipt or MovementKind.TransferIn or MovementKind.Return => +1,
        MovementKind.Issue or MovementKind.TransferOut or MovementKind.WriteOff => -1,
        _ => null,
    };

    /// <summary>Movements that MUST carry a reason: someone is asserting the records were wrong, and "no
    /// reason recorded" is what makes a ledger stop being evidence.</summary>
    public static bool RequiresReason(MovementKind kind) =>
        kind is MovementKind.Adjustment or MovementKind.WriteOff or MovementKind.Count;

    /// <summary>Movements that REDUCE on-hand and must therefore be validated against the current balance.</summary>
    public static bool ReducesStock(MovementKind kind) =>
        kind is MovementKind.Issue or MovementKind.TransferOut or MovementKind.WriteOff;

    /// <summary>
    /// Apply the kind's sign to a magnitude the caller supplied. Callers send a POSITIVE quantity and a kind;
    /// the sign is the schema's business, not theirs. An API that made clients send "-5 for an issue" would
    /// eventually receive "+5 for an issue" from one of them, and the ledger would be silently wrong in the
    /// direction nobody checks.
    /// </summary>
    public static decimal ApplySign(MovementKind kind, decimal magnitude) =>
        FixedSign(kind) is { } sign ? sign * Math.Abs(magnitude) : magnitude;

    /// <summary>
    /// Expired MEDICAL stock is QUARANTINED, not silently usable: an Issue against an expired batch is
    /// refused, and clearing it requires an explicit WriteOff with a reason.
    ///
    /// <para>The WriteOff exemption is the whole mechanism. If expiry blocked every movement, expired stock
    /// could never leave the ledger and would sit on the balance for ever; if it blocked nothing, it would be
    /// a label rather than a control. Blocking issue and permitting a reasoned write-off is what makes
    /// "quarantined" mean something.</para>
    ///
    /// <para>Inclusive, matching the licence gate: a batch marked "expires 30 September" is usable through
    /// 30 September. One boundary convention across the platform is worth more than each surface picking the
    /// one that felt right that day.</para>
    /// </summary>
    public static bool IsBatchExpired(DateOnly? expiryDate, DateOnly asOf) =>
        expiryDate is { } expiry && expiry < asOf;

    /// <summary>True when this movement must be refused because the batch has expired. WriteOff is exempt —
    /// it is the sanctioned way OUT. Return and Count are exempt too: unused stock coming back and a
    /// stock-take variance both have to be recordable against what is physically on the shelf, expired or
    /// not, or the ledger stops describing reality.</summary>
    public static bool RefuseForExpiry(MovementKind kind, DateOnly? expiryDate, DateOnly asOf) =>
        kind is not (MovementKind.WriteOff or MovementKind.Count or MovementKind.Return)
        && IsBatchExpired(expiryDate, asOf);

    /// <summary>
    /// Negative on-hand is impossible. Checked against the balance computed INSIDE the transaction that
    /// writes the movement (see <c>MovementService</c>, which takes a row lock first) — checking it outside
    /// would be a read that two concurrent issues of the last unit could both pass.
    /// </summary>
    public static bool WouldGoNegative(decimal onHand, decimal signedQuantity) =>
        onHand + signedQuantity < 0;

    /// <summary>A batch-tracked item's movements must name a batch. Without it, "on hand: 40" cannot answer
    /// which of them expire next month, and a recall cannot be scoped to the affected lot.</summary>
    public static bool RequiresBatch(bool isBatchTracked, Guid? batchId) => isBatchTracked && batchId is null;

    /// <summary>The paired movements a transfer becomes. Two rows sharing one ref, summing to ZERO, so nothing
    /// is created or destroyed in transit — asserted by test.</summary>
    public static (decimal Out, decimal In) TransferPair(decimal magnitude)
    {
        var m = Math.Abs(magnitude);
        return (-m, +m);
    }

    /// <summary>Expiry alert thresholds, mirroring the licence sweeper's 90/60/30 so a coordinator learns one
    /// cadence rather than two.</summary>
    public static readonly IReadOnlyList<int> ExpiryWarningDays = [90, 60, 30];

    /// <summary>Is this batch inside a warning window (or already expired) as at <paramref name="asOf"/>?</summary>
    public static bool IsExpiringWithin(DateOnly? expiryDate, DateOnly asOf, int days) =>
        expiryDate is { } expiry && expiry.DayNumber - asOf.DayNumber <= days;
}
