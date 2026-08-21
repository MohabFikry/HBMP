using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>
/// 32.1 — the half of "what is this patient already taking" that Mersal can answer from its own records.
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a query inlined into <c>HttpClinicalValidationPorts</c>, for two reasons. The obvious
/// one is layering: a class whose name is <c>Http…</c> should not hold a <c>DbContext</c>. The one that
/// actually forced it is that the ports have an offline test suite —
/// <c>DependencyDownYieldsUnavailableTests</c> — which constructs them directly with stub handlers to prove
/// that a dead dependency never reads as a clean result. A database dependency inlined there would have made
/// that suite need a database to prove a fact about HTTP.
/// </para>
/// <para>
/// The other half of the union is emr's <c>medication_history</c>, and it stays in the HTTP ports because it
/// is an HTTP call. Neither half is sufficient alone: this one cannot see a medicine bought at a pharmacy
/// Mersal has never heard of, and that one cannot stay current by itself.
/// </para>
/// </remarks>
public interface IPrescribedMedicationSource
{
    /// <summary>Active lines on this beneficiary's unexpired prescriptions, as active medications.</summary>
    Task<IReadOnlyList<ActiveMedication>> ActiveForAsync(
        Guid beneficiaryId, DateTimeOffset asOf, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class DbPrescribedMedicationSource(PharmacyDbContext db) : IPrescribedMedicationSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ActiveMedication>> ActiveForAsync(
        Guid beneficiaryId, DateTimeOffset asOf, CancellationToken ct = default)
        => await db.PrescriptionLines.AsNoTracking()
            .Where(l => db.Prescriptions.Any(p => p.PrescriptionId == l.PrescriptionId
                                                  && p.BeneficiaryId == beneficiaryId
                                                  && (p.ExpiresAt == null || p.ExpiresAt >= asOf)
                                                  && p.Status != RxStatus.Draft
                                                  && p.Status != RxStatus.Rejected
                                                  && p.Status != RxStatus.Cancelled))
            // Cancelled and Superseded lines are not being taken. A Dispensed one has been COLLECTED, which
            // is the strongest evidence the platform has that the patient is actually on it — excluding it
            // would drop precisely the medicines most certainly in the patient's hands.
            .Where(l => l.Status != RxLineStatus.Cancelled && l.Status != RxLineStatus.Superseded)
            .Select(l => new ActiveMedication(l.DrugId, l.DrugName ?? "(unnamed)", "Prescribed"))
            .ToListAsync(ct);
}
