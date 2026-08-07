using Mersal.ClinicalValidation;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>
/// Fetches everything <see cref="PrescriptionValidator"/> needs, as <see cref="Fetched{T}"/> values.
/// </summary>
/// <remarks>
/// <para>
/// This interface replaces <c>IPrescribingScreener</c> for the prescribing path, and the difference is the
/// whole point of phase 26. The old screener returned <c>AlertScreening</c> — a list of alerts — so "the
/// interaction service was unreachable" and "there are no interactions" were the same value: an empty list.
/// Its implementation then caught every <c>HttpRequestException</c> and treated every non-2xx response as a
/// clean result, which meant an outage rendered to the prescriber as a clean bill of health. Doc 43 §1 calls
/// that the single most dangerous line in the prescribing path.
/// </para>
/// <para>
/// Returning <see cref="Fetched{T}"/> makes the two answers different values. There is no empty collection
/// to mistake for a negative result, because an unreachable source produces no collection at all.
/// </para>
/// </remarks>
public interface IClinicalValidationPorts
{
    /// <summary>
    /// Gathers indications, interactions, allergies, dosing rules, the benefit pre-check and the encounter's
    /// diagnoses.
    /// </summary>
    /// <remarks>
    /// Implementations must map <b>every</b> failure mode — transport error, timeout, non-2xx response,
    /// unparseable body — to <c>Unavailable</c>. None of them is a clean result.
    /// </remarks>
    /// <param name="encounterId">
    /// When given, the diagnoses are read from the ENCOUNTER rather than accepted from the caller. This is
    /// what the authoritative step-2 path passes, and it is what closes the trusted-client hole design 44
    /// §1.3 describes: a forged diagnosis array in a submission then changes nothing.
    /// </param>
    /// <param name="clientDiagnoses">
    /// Only consulted when <paramref name="encounterId"/> is null — the step-1 advisory run while the doctor
    /// composes. The result is stamped <c>ClientSupplied</c> so a later divergence is explainable.
    /// </param>
    Task<ValidationSnapshot> FetchAsync(
        Guid beneficiaryId,
        IReadOnlyList<Guid> drugIds,
        Guid? encounterId,
        IReadOnlyList<string>? clientDiagnoses,
        string? bearerToken,
        CancellationToken ct = default);

    /// <summary>
    /// The catalogue's name for each drug, for the findings to refer to medicines by.
    /// </summary>
    /// <remarks>
    /// Master data's name, never one the client sent — the same rule the prescription-create path enforces,
    /// because a client-supplied label would let the medicine named in a safety warning differ from the drug
    /// actually prescribed. A drug whose name cannot be resolved is simply absent, and the caller falls back
    /// to what it already had rather than failing the validation over a display string.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, string>> DrugNamesAsync(
        IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct = default);
}

/// <summary>A catalogue product and its recorded active ingredient. Contains no patient data.</summary>
public sealed record DrugIngredient(Guid DrugId, string? ScientificName);

/// <summary>
/// Retrieves manufacturer product labels for the live interaction and dosing checks.
/// </summary>
/// <remarks>
/// Takes catalogue data only — a product id and an ingredient name. That is a deliberate boundary, not an
/// accident of the current caller: the implementation talks to a public third-party API, and an interface
/// that cannot express a beneficiary cannot leak one however it is later edited.
/// </remarks>
public interface IDrugLabelSource
{
    Task<Fetched<LabelEvidence>> FetchAsync(IReadOnlyList<DrugIngredient> drugs, CancellationToken ct = default);
}

/// <summary>
/// The benefit pre-check seam (doc 43 §5 step 1). Phase 27 replaces the implementation, not the shape.
/// </summary>
public interface IBenefitPreCheck
{
    Task<Fetched<IReadOnlyList<BenefitOutcome>>> EvaluateAsync(
        Guid beneficiaryId, IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct = default);
}

/// <summary>
/// Phase 26's benefit seam: reports that benefit rules are not evaluated at prescribing time yet.
/// </summary>
/// <remarks>
/// Returns an <b>available but empty</b> result, which the validator renders as <c>NotChecked</c> per line.
/// That distinction matters: <c>Unavailable</c> would claim a benefit service exists and failed, and there
/// is no such service until phase 27. "Not evaluated yet" is the true statement.
/// </remarks>
public sealed class NotYetImplementedBenefitPreCheck(TimeProvider clock) : IBenefitPreCheck
{
    public Task<Fetched<IReadOnlyList<BenefitOutcome>>> EvaluateAsync(
        Guid beneficiaryId, IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(Fetched.From<IReadOnlyList<BenefitOutcome>>(
            [],
            new ProvenanceInfo(
                // An injected clock rather than a bare wall-clock read: this timestamp is stored with the
                // prescription as the finding's provenance, so a test that pins what a prescriber was shown
                // has to be able to pin when it was checked.
                "Mersal benefit rules", "not-yet-configured", clock.GetUtcNow(),
                Caveat: "Benefit rules are evaluated on submission, not while prescribing.")));
}
