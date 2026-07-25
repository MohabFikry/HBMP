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

public sealed record ReferralResponse(
    Guid ReferralId, string ReferralNo, Guid BeneficiaryId, Guid EncounterId, Guid ReferringProviderId,
    string TargetSpecialty, Guid? TargetProviderId, string? Reason, string Status, DateTimeOffset RequestedAt)
{
    public static ReferralResponse From(Referral r) => new(
        r.ReferralId, r.ReferralNo, r.BeneficiaryId, r.EncounterId, r.ReferringProviderId,
        r.TargetSpecialty, r.TargetProviderId, r.Reason, r.Status.ToString(), r.RequestedAt);
}
