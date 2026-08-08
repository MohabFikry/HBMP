namespace Mersal.Pharmacy.Domain;

/// <summary>Why a dispense request was refused (mapped to problem+json at the edge). <c>None</c> means it passed
/// validation and may be applied.</summary>
public enum DispenseError { None, InvalidQuantity, LineNotFound, AlreadyDispensed, OverDispense, RxNotDispensable, ExpiredLot }

/// <summary>The pure dispensing rules (23-state-machines §3 "Pharmacy-specific guards"): a prescription may be
/// dispensed only while Approved/PartiallyDispensed and not past its validity window; a fully-dispensed or cancelled
/// line can NEVER be dispensed again (no-reuse); cumulative dispensed may never exceed prescribed; and no expired lot
/// may be dispensed. Validation is separated from the mutation so it is unit-testable without a DB — the
/// atomic/idempotent/duplicate-proof guarantees are enforced at the datastore (UNIQUE key + line xmin + CHECK).</summary>
public static class Dispensing
{
    public static DispenseError Validate(Prescription rx, Guid lineId, decimal quantity, DateOnly expiryDate, DateTimeOffset now)
    {
        // Rx reject rule: opening/dispensing an Expired/Cancelled/Rejected/fully-Dispensed Rx is refused.
        if (!PrescriptionWorkflow.IsDispensable(rx.Status)) return DispenseError.RxNotDispensable;
        if (rx.ExpiresAt is { } exp && exp <= now) return DispenseError.RxNotDispensable;

        if (quantity <= 0) return DispenseError.InvalidQuantity;
        var line = rx.Lines.FirstOrDefault(l => l.PrescriptionLineId == lineId);
        if (line is null) return DispenseError.LineNotFound;
        // no-reuse. Superseded joins the set in 30.1: the line was AMENDED, and dispensing it would hand
        // over the drug, dose or quantity the prescriber corrected — the amendment would have achieved
        // nothing, and the record would say it had.
        if (line.IsTerminal) return DispenseError.AlreadyDispensed;
        if (line.QuantityDispensed + quantity > line.QuantityPrescribed) return DispenseError.OverDispense;
        if (expiryDate <= DateOnly.FromDateTime(now.UtcDateTime)) return DispenseError.ExpiredLot;                   // no expired stock
        return DispenseError.None;
    }

    /// <summary>A line rolls up to Dispensed once its accumulator reaches the prescribed quantity, else PartiallyDispensed.</summary>
    public static RxLineStatus RecomputeLineStatus(PrescriptionLine line) =>
        line.QuantityDispensed >= line.QuantityPrescribed ? RxLineStatus.Dispensed : RxLineStatus.PartiallyDispensed;

    /// <summary>Prescription rolls up from its lines: all live lines Dispensed ⇒ Dispensed; any dispensing yet
    /// work remaining ⇒ PartiallyDispensed (remainder stays available for a later visit); otherwise unchanged.
    ///
    /// <para>A line that has LEFT the live set does not hold the prescription open. Cancelled has always been
    /// excluded; 30.1 adds Superseded, and it matters more: a superseded line is never Dispensed — it was
    /// replaced, not handed over — so counting it would strand the prescription in PartiallyDispensed for
    /// ever and <c>RxDispensed</c> would never emit. Its accumulator is not counted either, because the
    /// successor carries it forward.</para></summary>
    public static RxStatus RecomputePrescriptionStatus(Prescription rx)
    {
        var live = rx.Lines
            .Where(l => l.Status is not (RxLineStatus.Cancelled or RxLineStatus.Superseded)).ToList();
        if (live.Count > 0 && live.All(l => l.Status == RxLineStatus.Dispensed))
            return RxStatus.Dispensed;
        if (live.Any(l => l.QuantityDispensed > 0))
            return RxStatus.PartiallyDispensed;
        return rx.Status;
    }
}

/// <summary>Substitution guard (23-state-machines §3, US-052): a pharmacist may dispense a different drug ONLY when it
/// is a policy-approved alternative for the prescribed drug (from masterdata/PBM). Dispensing the prescribed drug
/// itself is trivially allowed. Anything off-list is blocked and routed to approvals — never dispensed off-list.</summary>
public static class SubstitutionPolicy
{
    public static bool IsApproved(Guid prescribedDrugId, Guid substitutedDrugId, IReadOnlyCollection<Guid> approvedAlternatives) =>
        substitutedDrugId == prescribedDrugId || approvedAlternatives.Contains(substitutedDrugId);
}
