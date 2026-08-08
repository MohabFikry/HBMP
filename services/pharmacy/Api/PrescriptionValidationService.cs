using System.Text.Json;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// Runs a prescription validation and records it (phase 26.4).
/// </summary>
/// <remarks>
/// Used by BOTH steps of doc 43 §5 — the advisory <c>/validate</c> call while the doctor composes, and the
/// authoritative re-run the server performs on submit. Deliberately one code path: two would drift, and a
/// step 2 that disagreed with step 1 for the wrong reason would be worse than no step 2 at all.
/// </remarks>
public sealed class PrescriptionValidationService(
    IClinicalValidationPorts ports, TimeProvider clock)
{
    /// <summary>
    /// Bumped whenever the engine's behaviour changes, and stored with every run.
    /// </summary>
    /// <remarks>
    /// Six months later "why did this not warn?" needs an answer that survives the engine being changed
    /// since. Without a version stamped on the run, the only available answer is a guess.
    /// </remarks>
    public const string EngineVersion = "28.2";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="authoritative">
    /// TRUE on the submit path (step 2). The diagnoses are then read from the ENCOUNTER and
    /// <paramref name="clientDiagnoses"/> is not consulted at all — design 44 §1.3. FALSE for the advisory
    /// step-1 run, which may use the composing screen's list for speed and marks the run accordingly.
    /// </param>
    public async Task<(ValidationResult Result, ValidationRequest Request, DiagnosisContext Diagnoses)> EvaluateAsync(
        Guid beneficiaryId, Guid encounterId, IReadOnlyList<CreateRxLine> lines,
        IReadOnlyList<string> clientDiagnoses, string? bearerToken, CancellationToken ct,
        bool authoritative = false)
    {
        var drugIds = lines.Select(l => l.DrugId).Distinct().ToList();

        // The medicine's NAME, from master data. Findings refer to drugs by it — "interaction with
        // Cordarone 200mg" — and the field previously fell back to the DOSE, so a cross-line interaction
        // warning read "interaction with 5mg". A warning that cannot name the other medicine is one a
        // prescriber cannot act on, and the fallback made that failure invisible because it still produced a
        // plausible-looking string.
        var names = await ports.DrugNamesAsync(drugIds, bearerToken, ct);

        var inputs = lines
            .Select(l => new PrescriptionLineInput(
                l.ClientLineId ?? Guid.NewGuid(), l.DrugId,
                names.TryGetValue(l.DrugId, out var name) ? name : l.Dose ?? l.DrugId.ToString(),
                DoseText: l.Dose, DoseAmount: l.DoseAmount, DoseUnit: l.DoseUnit,
                TimesPerDay: l.TimesPerDay, DurationDays: l.DurationDays,
                Quantity: (int?)l.QuantityPrescribed))
            .ToList();
        var snapshot = await ports.FetchAsync(
            beneficiaryId, drugIds,
            // The whole of 28.2 in one argument. On the authoritative path the encounter id is what the
            // diagnoses are read from; the caller's list is not passed on at all, so there is no code path
            // by which a request body could reach the engine's most important input.
            authoritative ? encounterId : null,
            authoritative ? null : clientDiagnoses,
            bearerToken, ct);

        // Active medications are not yet sourced on this path; the interaction check still runs across the
        // lines being written. Left explicit rather than hidden behind a default.
        var request = new ValidationRequest(encounterId, inputs, []);

        // The diagnoses the engine ACTUALLY used, handed back so the caller records what was checked rather
        // than what was sent. An Unavailable source yields an empty list with EncounterFetched provenance:
        // the findings already say the check could not run, and inventing a list here would contradict them.
        var diagnoses = snapshot.Diagnoses is Fetched<DiagnosisContext>.Available d
            ? d.Value
            : new DiagnosisContext([], DiagnosisProvenance.EncounterFetched);

        return (PrescriptionValidator.Validate(request, snapshot, clock.GetUtcNow()), request, diagnoses);
    }

    /// <summary>Records the run. Append-only — never updated, never deleted.</summary>
    public PrescriptionValidationRun ToRun(
        ValidationResult result, ValidationRequest request, Guid beneficiaryId,
        Guid? prescriptionId, string step, string? ranBy) =>
        new()
        {
            ValidationId = Guid.NewGuid(),
            PrescriptionId = prescriptionId,
            EncounterId = request.EncounterId,
            BeneficiaryId = beneficiaryId,
            RanAt = result.RanAt,
            RanBy = ranBy,
            Step = step,
            EngineVersion = EngineVersion,
            OverallState = Overall(result).ToString(),
            Findings = JsonSerializer.Serialize(result.Findings.Select(ToView), Json),
        };

    /// <summary>
    /// The single worst state across every line — the summary a reviewer sees first.
    /// </summary>
    /// <remarks>
    /// Same precedence as a line's own summary: Blocked, then Unavailable, then Warning, then NotChecked,
    /// then Ok. Unavailable deliberately outranks Warning, so a run in which something could not be checked
    /// never summarises as merely "has warnings".
    /// </remarks>
    public static CheckState Overall(ValidationResult result)
    {
        var states = result.Findings.Select(f => f.State).ToList();
        if (states.Count == 0) return CheckState.NotChecked;
        foreach (var rank in new[]
                 { CheckState.Blocked, CheckState.Unavailable, CheckState.Warning, CheckState.NotChecked })
        {
            if (states.Contains(rank)) return rank;
        }
        return CheckState.Ok;
    }

    public static FindingView ToView(Finding f) => new(
        f.LineId, f.DrugId, f.Kind.ToString(), f.State.ToString(),
        f.MessageEn, f.MessageAr,
        f.Provenance?.SourceName, f.Provenance?.SourceVersion, f.Provenance?.RetrievedAt, f.Provenance?.Caveat,
        f.Severity?.ToString(), f.RelatedLineId,
        f.RequiresAcknowledgement, f.IsBlocking, f.ReferenceText);

    public static ValidationResultView ToView(
        ValidationResult result, ValidationRequest request, Guid validationId) => new(
        validationId, result.RanAt, EngineVersion, Overall(result).ToString(),
        [.. result.Findings.Select(ToView)],
        request.Lines.ToDictionary(l => l.LineId, l => result.StateFor(l.LineId).ToString()));
}
