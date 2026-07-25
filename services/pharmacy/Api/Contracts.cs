using Mersal.Pharmacy.Domain;

namespace Mersal.Pharmacy.Api;

/// <summary>Create + submit an e-prescription (US-033). Created Draft then transitioned to Submitted; advisory
/// interaction/allergy alerts are surfaced. <see cref="AcknowledgeAlerts"/> records a prescriber override.</summary>
public sealed record CreatePrescriptionRequest(
    Guid BeneficiaryId, Guid EncounterId, DateTimeOffset? ExpiresAt, bool AcknowledgeAlerts, List<CreateRxLine> Lines);

public sealed record CreateRxLine(
    Guid DrugId, string? Dose, string? Route, string? Frequency, decimal QuantityPrescribed, int RefillsAllowed);

public sealed record CreateReferralRequest(
    Guid BeneficiaryId, Guid EncounterId, string TargetSpecialty, Guid? TargetProviderId, string? Reason);

public sealed record CancelRequest(string? Reason);

public sealed record RxLineResponse(
    Guid PrescriptionLineId, Guid DrugId, string? Dose, string? Route, string? Frequency,
    decimal QuantityPrescribed, decimal QuantityDispensed, int RefillsAllowed, string Status)
{
    public static RxLineResponse From(PrescriptionLine l) => new(
        l.PrescriptionLineId, l.DrugId, l.Dose, l.Route, l.Frequency,
        l.QuantityPrescribed, l.QuantityDispensed, l.RefillsAllowed, l.Status.ToString());
}

public sealed record AlertView(string Kind, string Severity, string Detail);

public sealed record PrescriptionResponse(
    Guid PrescriptionId, string RxNo, Guid BeneficiaryId, Guid EncounterId, Guid PrescriberId,
    string Status, bool Dispensable, DateTimeOffset? SubmittedAt, DateTimeOffset? ExpiresAt,
    IReadOnlyList<RxLineResponse> Lines, IReadOnlyList<AlertView> Alerts)
{
    public static PrescriptionResponse From(Prescription p, IReadOnlyList<AlertView>? alerts = null) => new(
        p.PrescriptionId, p.RxNo, p.BeneficiaryId, p.EncounterId, p.PrescriberId, p.Status.ToString(),
        PrescriptionWorkflow.IsDispensable(p.Status), p.SubmittedAt, p.ExpiresAt,
        p.Lines.Select(RxLineResponse.From).ToList(), alerts ?? []);
}

// ---- Phase 6 dispensing (min-necessary: drug/dose/route/frequency + remaining qty + patient id ONLY; never
// diagnoses/notes/investigation results) ----

/// <summary>A dispensable line for the pharmacist queue — only what is needed to dispense. Fully-dispensed/cancelled
/// lines are omitted by the projection.</summary>
public sealed record DispensableLineView(
    Guid PrescriptionLineId, Guid DrugId, string? Dose, string? Route, string? Frequency,
    decimal QuantityPrescribed, decimal QuantityDispensed, decimal QuantityRemaining, string Status);

public sealed record DispensableRxView(
    Guid PrescriptionId, string RxNo, Guid BeneficiaryId, string Status, DateTimeOffset? ExpiresAt,
    IReadOnlyList<DispensableLineView> Lines)
{
    public static DispensableRxView From(Prescription p) => new(
        p.PrescriptionId, p.RxNo, p.BeneficiaryId, p.Status.ToString(), p.ExpiresAt,
        p.Lines.Where(l => l.Status is RxLineStatus.Active or RxLineStatus.PartiallyDispensed && l.QuantityRemaining > 0)
            .Select(l => new DispensableLineView(
                l.PrescriptionLineId, l.DrugId, l.Dose, l.Route, l.Frequency,
                l.QuantityPrescribed, l.QuantityDispensed, l.QuantityRemaining, l.Status.ToString()))
            .ToList());
}

/// <summary>Dispense a quantity of a line against a batch/lot with its expiry. <see cref="SubstitutedDrugId"/> +
/// <see cref="SubstitutionReason"/> record a policy-approved substitution (6.3); omit them for a straight dispense.</summary>
public sealed record DispenseRequest(
    decimal Quantity, string BatchNo, DateOnly ExpiryDate, Guid? SubstitutedDrugId, string? SubstitutionReason);

public sealed record DispenseEventView(
    Guid DispenseId, Guid PrescriptionLineId, decimal Quantity, string BatchNo, DateOnly ExpiryDate,
    Guid? SubstitutedDrugId, string? SubstitutionReason, DateTimeOffset DispensedAt)
{
    public static DispenseEventView From(DispenseEvent d) => new(
        d.DispenseId, d.PrescriptionLineId, d.Quantity, d.BatchNo, d.ExpiryDate,
        d.SubstitutedDrugId, d.SubstitutionReason, d.DispensedAt);
}

public sealed record DispenseResponse(string RxStatus, bool Replayed, DispenseEventView Dispense, DispensableRxView Prescription)
{
    public static DispenseResponse From(Prescription rx, DispenseEvent evt, bool replayed) => new(
        rx.Status.ToString(), replayed, DispenseEventView.From(evt), DispensableRxView.From(rx));
}

public sealed record OutOfStockRequest(Guid PrescriptionLineId, decimal? Quantity, string? Note);

public sealed record ReferralResponse(
    Guid ReferralId, string ReferralNo, Guid BeneficiaryId, Guid EncounterId, Guid ReferringProviderId,
    string TargetSpecialty, Guid? TargetProviderId, string? Reason, string Status, DateTimeOffset RequestedAt)
{
    public static ReferralResponse From(Referral r) => new(
        r.ReferralId, r.ReferralNo, r.BeneficiaryId, r.EncounterId, r.ReferringProviderId,
        r.TargetSpecialty, r.TargetProviderId, r.Reason, r.Status.ToString(), r.RequestedAt);
}
