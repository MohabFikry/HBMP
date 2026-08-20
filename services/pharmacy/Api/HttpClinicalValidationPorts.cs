using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Pharmacy.Infrastructure;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// Fetches the clinical facts for a prescribing validation from masterdata and emr.
/// </summary>
/// <remarks>
/// <para>
/// The contract this class exists to honour: <b>no failure is a clean result</b>. Transport error, timeout,
/// 403, 500, unparseable body — each becomes an <c>Unavailable</c> naming what went wrong. The prescriber
/// sees "check unavailable"; they never see a tick that means "we could not ask".
/// </para>
/// <para>
/// The code this replaces caught <c>HttpRequestException</c> and returned no alerts, and — less visibly —
/// treated any non-2xx as no alerts through a bare <c>if (resp.IsSuccessStatusCode)</c>. That second form is
/// the more dangerous of the two, because it looks like handling. After phase 26.1 scoped masterdata on
/// <c>masterdata:read</c>, a token missing the scope would have taken exactly that path: a 403 silently
/// rendered as "no interactions found".
/// </para>
/// </remarks>
public sealed class HttpClinicalValidationPorts(
    IHttpClientFactory factory,
    IBenefitPreCheck benefitPreCheck,
    IDrugLabelSource labels,
    IPrescribedMedicationSource prescribed,
    TimeProvider clock) : IClinicalValidationPorts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A bounded wait per source.
    /// </summary>
    /// <remarks>
    /// A clinical check must not hold the encounter open. The platform has no resilience layer (no Polly, no
    /// retry, bare AddHttpClient — doc 43 §0), and a hanging dependency without a deadline is the difference
    /// between "check unavailable" and a doctor staring at a spinner mid-consultation. A timeout here is an
    /// answer: Unavailable.
    /// </remarks>
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Everything the engine needs, fetched concurrently.
    /// </summary>
    /// <param name="encounterId">
    /// When given, the diagnoses are read from the ENCOUNTER — the authoritative path (28.2). This is what
    /// step 2 passes, and it is why a forged <c>diagnosisIcdCodes</c> in a submission changes nothing.
    /// </param>
    /// <param name="clientDiagnoses">
    /// The list the composing screen is holding. Used only when <paramref name="encounterId"/> is null (the
    /// step-1 advisory run) and stamped <see cref="DiagnosisProvenance.ClientSupplied"/>, so a step-1/step-2
    /// divergence has a recorded explanation instead of being mysterious.
    /// </param>
    public async Task<ValidationSnapshot> FetchAsync(
        Guid beneficiaryId, IReadOnlyList<Guid> drugIds, Guid? encounterId,
        IReadOnlyList<string>? clientDiagnoses, string? bearerToken, CancellationToken ct = default)
    {
        // Concurrently: six independent sources, and one slow source must not serialise behind another.
        var indications = FetchIndicationsAsync(drugIds, bearerToken, ct);
        var interactions = FetchInteractionsAsync(drugIds, bearerToken, ct);
        var allergies = FetchAllergiesAsync(beneficiaryId, drugIds, bearerToken, ct);
        var benefit = benefitPreCheck.EvaluateAsync(beneficiaryId, drugIds, bearerToken, ct);
        var labels = FetchLabelsAsync(drugIds, bearerToken, ct);
        var diagnoses = FetchDiagnosesAsync(encounterId, clientDiagnoses, bearerToken, ct);
        var compositions = FetchCompositionsAsync(drugIds, bearerToken, ct);
        var patient = FetchPatientAsync(beneficiaryId, encounterId, bearerToken, ct);
        var packFacts = FetchPackFactsAsync(drugIds, bearerToken, ct);   // 29.6
        var activeMedications = FetchActiveMedicationsAsync(beneficiaryId, bearerToken, ct);   // 32.1

        await Task.WhenAll(
            indications, interactions, allergies, benefit, labels, diagnoses, compositions, patient,
            packFacts, activeMedications);

        // The one source that CANNOT join the parallel wave: a contraindication is a question about the
        // patient's conditions, so it needs the diagnoses and the pregnancy status before it can be asked.
        // Sequenced deliberately rather than given its own diagnosis fetch — two fetches of the same list
        // could disagree, and the check would then be screening against a different set of conditions from
        // the one the indication check used.
        // Both of these depend on the diagnoses and the patient, so they run after that wave rather than
        // fetching their own copies — two fetches of one list can disagree, and the checks would then be
        // reasoning about different patients.
        var contraindications = await FetchContraindicationsAsync(
            drugIds, await diagnoses, await patient, bearerToken, ct);
        var dosingRules = await FetchDosingRulesAsync(
            drugIds, await diagnoses, await patient, bearerToken, ct);

        return new ValidationSnapshot(
            await indications, await interactions, await allergies, dosingRules, await benefit,
            await labels, await diagnoses, await compositions, await patient, contraindications,
            await packFacts, await activeMedications);
    }

    /// <summary>
    /// 32.1 — what the beneficiary is already taking: active Mersal lines UNION reported history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prescription half is a LOCAL query. Pharmacy owns that table, and crossing a service boundary to
    /// ask itself a question would be slower and no more correct. The history half is emr's, because a
    /// medicine bought at a pharmacy Mersal has never heard of cannot be derived from Mersal's own records —
    /// which is the whole reason <c>medication_history</c> carries a <c>SelfReported</c> and an
    /// <c>External</c> source.
    /// </para>
    /// <para>
    /// <b>Either half failing makes the WHOLE fact Unavailable.</b> Returning the prescriptions alone when
    /// emr is down would hand the engine a list that looks complete, and the engine would then report Ok
    /// against it. A half-list presented as a whole one is the exact failure <c>Fetched&lt;T&gt;</c> exists
    /// to prevent, and it is worse than no list because it carries the authority of a completed check.
    /// </para>
    /// </remarks>
    private async Task<Fetched<ActiveMedications>> FetchActiveMedicationsAsync(
        Guid beneficiaryId, string? bearerToken, CancellationToken ct)
        => await GuardedAsync<ActiveMedications>("current medication list", async token =>
        {
            var mersalsOwn = await prescribed.ActiveForAsync(beneficiaryId, clock.GetUtcNow(), token);

            var history = await GetAsync<List<MedicationHistoryDto>>(
                "emr", $"/api/v1/beneficiaries/{beneficiaryId}/medication-history?status=Active",
                bearerToken, token) ?? [];

            var items = new List<ActiveMedication>(mersalsOwn);
            items.AddRange(history.Select(h => new ActiveMedication(h.DrugId, h.DrugName ?? "(unnamed)", h.Source)));

            return ((ActiveMedications)new ActiveMedications(items),
                new ProvenanceInfo("Mersal prescriptions + recorded medication history", "live", clock.GetUtcNow()));
        }, ct);

    private sealed record MedicationHistoryDto(Guid DrugId, string? DrugName, string Source);

    /// <summary>
    /// The encounter's diagnoses, read from emr rather than taken from the request body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Design 44 §1.3: step 2 re-ran every check server-side and then trusted the client for its most
    /// important input. The server now reads the encounter; the client's copy is display state.
    /// </para>
    /// <para>
    /// <b>emr unreachable is Unavailable, not NotChecked.</b> "No diagnosis is recorded on this encounter"
    /// and "we could not find out what is recorded" are different statements about different things, and
    /// only one of them is the encounter's fault. Falling back to the client's list on an outage would
    /// reopen the hole precisely when the server is least able to notice.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What each prescribed product is made of — its molecules and its ATC-4 class.
    /// </summary>
    /// <remarks>
    /// The same catalogue call the label lookup already makes, read for a second purpose. Duplicate therapy
    /// is a question about MOLECULES: two trade names holding one is the commonest real prescribing
    /// duplication, and the paracetamol hidden inside a cold-and-flu compound is only visible here.
    /// </remarks>
    private async Task<Fetched<IReadOnlyDictionary<Guid, DrugComposition>>> FetchCompositionsAsync(
        IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct)
        => await GuardedAsync<IReadOnlyDictionary<Guid, DrugComposition>>("drug catalogue", async token =>
        {
            var body = await PostAsync<IngredientsDto>(
                "masterdata", "/api/v1/drugs/ingredients/by-ids", new { drugIds }, bearerToken, token);

            var map = (body?.Items ?? []).ToDictionary(
                i => i.DrugId,
                i => new DrugComposition(
                    i.DrugId,
                    i.IngredientKeys ?? [],
                    // ATC-4 is the therapeutic class two different molecules can share — M01A for the
                    // NSAIDs, A02BC for the PPIs. Shorter codes are anatomical groupings far too broad to
                    // call a duplication, so anything under four characters is no class at all.
                    i.AtcCode is { Length: >= 4 } atc ? atc[..4].ToUpperInvariant() : null));

            return (map, new ProvenanceInfo("Mersal drug catalogue", "live", clock.GetUtcNow()));
        }, ct);

    /// <summary>
    /// 29.6 — the pack facts the quantity check computes from (design 45 §6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A drug ABSENT from the returned map is a drug whose pack the catalogue does not describe, and the
    /// check reports that as NotChecked naming the field. That is deliberately different from this whole
    /// fetch failing, which is Unavailable — "master data does not record it" and "we could not ask master
    /// data" send the prescriber to two different places.
    /// </para>
    /// <para>
    /// Null fields are carried through as null rather than dropped or defaulted. Defaulting
    /// <c>is_pack_splittable</c> either way is exactly the guess invariant 8 forbids.
    /// </para>
    /// </remarks>
    private async Task<Fetched<IReadOnlyDictionary<Guid, DrugPackFacts>>> FetchPackFactsAsync(
        IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct)
        => await GuardedAsync<IReadOnlyDictionary<Guid, DrugPackFacts>>("drug catalogue", async token =>
        {
            var body = await PostAsync<PackFactsDto>(
                "masterdata", "/api/v1/drugs/pack-facts/by-ids", new { drugIds }, bearerToken, token);

            var map = (body?.Items ?? []).ToDictionary(
                i => i.DrugId,
                i => new DrugPackFacts(i.IsPackSplittable, i.PackSize, i.PackContent));

            return ((IReadOnlyDictionary<Guid, DrugPackFacts>)map,
                new ProvenanceInfo("Mersal drug catalogue", "live", clock.GetUtcNow()));
        }, ct);

    private sealed record PackFactsDto(List<PackFactRow>? Items);
    private sealed record PackFactRow(Guid DrugId, bool? IsPackSplittable, decimal? PackSize, decimal? PackContent);

    private async Task<Fetched<DiagnosisContext>> FetchDiagnosesAsync(
        Guid? encounterId, IReadOnlyList<string>? clientDiagnoses, string? bearerToken, CancellationToken ct)
    {
        if (encounterId is null)
        {
            // Step 1, the advisory run. The provenance is what makes this legible later.
            return Fetched.From(
                new DiagnosisContext(clientDiagnoses ?? [], DiagnosisProvenance.ClientSupplied),
                new ProvenanceInfo(
                    "Prescribing screen (client-supplied)", "step-1", clock.GetUtcNow(),
                    Caveat: "Advisory run: the diagnoses came from the composing screen. The server re-reads "
                            + "them from the encounter when the prescription is submitted."));
        }

        return await GuardedAsync("encounter diagnoses", async token =>
        {
            var body = await GetAsync<ValidationContextDto>(
                "emr", $"/api/v1/encounters/{encounterId}/validation-context", bearerToken, token);

            var codes = (body?.Diagnoses ?? [])
                .Select(d => d.IcdCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The hierarchy the indication check walks (28.7). Best-effort: a catalogue that cannot supply
            // ancestors leaves the chains empty, and the engine falls back to the category comparison rather
            // than warning off-label on every prescription. Failing the whole diagnosis fetch over a missing
            // ancestor row would turn a degraded check into an absent one.
            var ancestors = await AncestorsAsync(codes, bearerToken, token);

            return (new DiagnosisContext(codes, DiagnosisProvenance.EncounterFetched, ancestors),
                new ProvenanceInfo("EMR encounter diagnoses", "live", clock.GetUtcNow()));
        }, ct);
    }

    /// <summary>
    /// The patient's age, weight and pregnancy status — the context a dose check needs (28.8, doc 44 §4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The weight comes from the SAME emr call as the diagnoses, so patient context costs one request and
    /// one audited PHI read rather than three. The age comes from patient-service, which holds the
    /// beneficiary's date of birth — and only the derived AGE crosses into the engine, never the date
    /// itself: a dose band needs "4 years old", not a birthday.
    /// </para>
    /// <para>
    /// Without an encounter there is nothing to read, and the step-1 advisory run says so rather than
    /// inventing an adult. A weight-based rule then reports the missing input, which is the honest answer.
    /// </para>
    /// </remarks>
    private async Task<Fetched<PatientContext>> FetchPatientAsync(
        Guid beneficiaryId, Guid? encounterId, string? bearerToken, CancellationToken ct)
    {
        if (encounterId is null)
        {
            return Fetched.From(
                new PatientContext(),
                new ProvenanceInfo(
                    "Patient context", "step-1", clock.GetUtcNow(),
                    Caveat: "Advisory run: age and weight are read from the encounter when the prescription "
                            + "is submitted."));
        }

        return await GuardedAsync("patient record", async token =>
        {
            var context = await GetAsync<ValidationContextDto>(
                "emr", $"/api/v1/encounters/{encounterId}/validation-context", bearerToken, token);

            // Age is a separate service and a softer requirement: a failure here leaves the age null, which
            // makes the paediatric rules report a missing input rather than failing the whole fetch.
            int? ageYears = null;
            try
            {
                var person = await GetAsync<BeneficiaryDto>(
                    "patient", $"/api/v1/beneficiaries/{beneficiaryId}", bearerToken, token);
                if (person?.DateOfBirth is { } dob)
                {
                    var today = clock.GetUtcNow();
                    ageYears = today.Year - dob.Year - (today.DayOfYear < dob.DayOfYear ? 1 : 0);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ageYears = null;
            }

            return (new PatientContext(
                    ageYears,
                    AgeMonths: null,
                    context?.WeightKg,
                    context?.WeightMeasuredAt,
                    // Explicitly Unknown, and it will stay Unknown until laboratory results are stored as
                    // structured values. The engine reports that rather than dosing around it.
                    RenalFunction.Unknown,
                    PregnancyStatus.Unknown),
                new ProvenanceInfo("EMR vitals + beneficiary record", "live", clock.GetUtcNow()));
        }, ct);
    }

    /// <summary>
    /// The medicine against conditions the patient actually has (28.9, doc 44 §5).
    /// </summary>
    /// <remarks>
    /// If the diagnoses could not be read, this check is <c>Unavailable</c> too, and it must NOT fall back to
    /// an empty condition list — that would report "no contraindication found" for a patient whose conditions
    /// are unknown, which is exactly the false assurance this phase exists to remove.
    /// </remarks>
    private async Task<Fetched<ContraindicationTable>> FetchContraindicationsAsync(
        IReadOnlyList<Guid> drugIds, Fetched<DiagnosisContext> diagnoses, Fetched<PatientContext> patient,
        string? bearerToken, CancellationToken ct)
    {
        if (diagnoses is Fetched<DiagnosisContext>.Unavailable du)
        {
            return Fetched.NotAvailable<ContraindicationTable>(
                $"the encounter's conditions could not be read ({du.Reason})");
        }

        var codes = ((Fetched<DiagnosisContext>.Available)diagnoses).Value.IcdCodes;

        // Pregnancy counts only when it is RECORDED as such. Unknown is not pregnant: a rule firing on every
        // patient nobody has asked about would be dismissed within a day and stop meaning anything for the
        // patients it was written for.
        var isPregnant = patient is Fetched<PatientContext>.Available p
                         && p.Value.Pregnancy == PregnancyStatus.Pregnant;

        return await GuardedAsync("contraindication list", async token =>
        {
            var body = await PostAsync<ContraindicationsDto>(
                "masterdata", "/api/v1/contraindications/check-by-ids",
                new { drugIds, diagnosisIcdCodes = codes, isPregnant }, bearerToken, token);

            var hits = (body?.Items ?? [])
                .Select(i => new ContraindicationFact(
                    i.DrugId, i.MatchedOn ?? "is contraindicated in a recorded condition",
                    // An unrecognised severity errs interruptive. A contraindication graded in a vocabulary
                    // this port does not know is not one to quietly downgrade.
                    Enum.TryParse<ClinicalSeverity>(i.Severity, out var sev) ? sev : ClinicalSeverity.Major,
                    i.MechanismEn ?? "", i.MechanismAr ?? "",
                    i.ClinicalEffectEn ?? "", i.ClinicalEffectAr ?? "",
                    i.ManagementEn ?? "", i.ManagementAr ?? "", i.Citation))
                .ToList();

            return (new ContraindicationTable(hits, body?.KnownRuleCount ?? 0),
                new ProvenanceInfo(
                    "Mersal contraindication list", "curated", clock.GetUtcNow(),
                    Caveat: $"Checked against Mersal's own contraindication list "
                            + $"({body?.KnownRuleCount ?? 0} rules); coverage is partial."));
        }, ct);
    }

    /// <summary>Each diagnosis code's ancestor chain, for the descendant-or-self match (28.7, doc 44 §6).</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> AncestorsAsync(
        IReadOnlyList<string> codes, string? bearerToken, CancellationToken ct)
    {
        if (codes.Count == 0) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        try
        {
            var body = await PostAsync<IcdAncestorsDto>(
                "masterdata", "/api/v1/icd-codes/ancestors", new { codes }, bearerToken, ct);

            return (body?.Items ?? []).ToDictionary(
                i => i.Code, i => (IReadOnlyList<string>)(i.Ancestors ?? []), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Degraded, not failed. The check still runs at category level and says nothing untrue.
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, string>> DrugNamesAsync(
        IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerSourceTimeout);

            var body = await PostAsync<IngredientsDto>(
                "masterdata", "/api/v1/drugs/ingredients/by-ids", new { drugIds }, bearerToken, cts.Token);

            return (body?.Items ?? [])
                .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                .ToDictionary(i => i.DrugId, i => i.Name!);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A display name is not worth failing a safety check over. The caller keeps what it had.
            return new Dictionary<Guid, string>();
        }
    }

    /// <summary>
    /// Manufacturer labels, matched by active ingredient.
    /// </summary>
    /// <remarks>
    /// Two hops on purpose. The ingredient names come from our own catalogue, and only the ingredient goes
    /// on to the external source — the drug ids never leave the platform, and neither does anything about
    /// the patient. If the catalogue hop fails there is nothing to look labels up by, so the whole source
    /// reports unavailable rather than silently checking a subset.
    /// </remarks>
    private async Task<Fetched<LabelEvidence>> FetchLabelsAsync(
        IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct)
    {
        List<DrugIngredient> ingredients;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerSourceTimeout);

            var body = await PostAsync<IngredientsDto>(
                "masterdata", "/api/v1/drugs/ingredients/by-ids", new { drugIds }, bearerToken, cts.Token);

            ingredients = (body?.Items ?? [])
                .Select(i => new DrugIngredient(i.DrugId, i.ScientificName))
                .ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Fetched.NotAvailable<LabelEvidence>("the drug catalogue could not be reached");
        }

        return await labels.FetchAsync(ingredients, ct);
    }

    private async Task<Fetched<IReadOnlyDictionary<Guid, DrugIndicationFact>>> FetchIndicationsAsync(
        IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct)
        => await GuardedAsync<IReadOnlyDictionary<Guid, DrugIndicationFact>>("indication list", async token =>
        {
            var body = await PostAsync<IndicationsDto>(
                "masterdata", "/api/v1/drug-indications/by-ids", new { drugIds }, bearerToken, token);

            var facts = (body?.Items ?? [])
                .ToDictionary(i => i.DrugId, i => new DrugIndicationFact(i.DrugId, i.IcdCategories ?? []));

            return (facts, new ProvenanceInfo(
                body?.Source is { Length: > 0 } s ? $"Drug indication list ({s})" : "Drug indication list",
                body?.SourceRelease ?? "unknown",
                clock.GetUtcNow(),
                // Doc 43 §1 rule 2, and the source's own Notes sheet: the ATC→ICD step is clinical judgement,
                // not a published dataset. A prescriber weighing an off-label warning needs to know that.
                Caveat: "Indications are mapped at ATC level 4 by clinical review, not from a published dataset."));
        }, ct);

    private async Task<Fetched<InteractionTable>> FetchInteractionsAsync(
        IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct)
        => await GuardedAsync("interaction list", async token =>
        {
            var body = await PostAsync<InteractionsDto>(
                "masterdata", "/api/v1/drug-interactions/check-by-ids", new { drugIds }, bearerToken, token);

            var pairs = (body?.Interactions ?? [])
                .Select(i => new InteractionFact(
                    i.DrugAId, i.DrugBId,
                    // An unrecognised severity errs INTERRUPTIVE rather than inline. A rule masterdata
                    // grades in a vocabulary this port does not know is not a rule to quietly downgrade.
                    Enum.TryParse<ClinicalSeverity>(i.Severity, out var sev) ? sev : ClinicalSeverity.Major,
                    i.Description, i.MatchedOn,
                    i.MechanismEn, i.MechanismAr,
                    i.ClinicalEffectEn, i.ClinicalEffectAr,
                    i.ManagementEn, i.ManagementAr,
                    i.Onset, i.EvidenceLevel, i.Citation))
                .ToList();

            var table = new InteractionTable(
                pairs, body?.KnownPairCount ?? 0, body?.RulesUpdatedAt,
                (body?.UnresolvableDrugIds ?? []).ToHashSet());

            return (table, new ProvenanceInfo(
                "Mersal interaction list", body?.SourceRelease ?? "curated",
                clock.GetUtcNow(),
                // D3 and doc 44 §8: curated internally, so coverage is partial by construction. Stating the
                // SIZE and the DATE is the difference between an honest check and an implied guarantee — a
                // partial list is ethical to ship only when its partiality is visible.
                Caveat: $"Checked against Mersal's own interaction list ({table.KnownPairCount} ingredient "
                        + $"pairs{(body?.RulesUpdatedAt is { } d ? $", updated {d:d MMM yyyy}" : "")}); "
                        + "coverage is partial."));
        }, ct);

    private async Task<Fetched<AllergyScreen>> FetchAllergiesAsync(
        Guid beneficiaryId, IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct)
        => await GuardedAsync("allergy record", async token =>
        {
            var recorded = await GetAsync<AllergyDto[]>(
                "emr", $"/api/v1/beneficiaries/{beneficiaryId}/allergies", bearerToken, token) ?? [];
            var allergenIds = recorded.Select(a => a.AllergenId).ToArray();

            var conflicts = new List<AllergyConflict>();

            // What the matcher could NOT evaluate, carried through rather than discarded.
            //
            // This method previously read one field of the response — `conflict` — and threw the rest away,
            // which meant "no conflict" and "could not compare anything" arrived at the engine as the same
            // empty list. masterdata's matcher was structurally incapable of ever matching (doc 44 §1.1), so
            // that empty list was the whole population, and the engine reported Ok on all of it.
            //
            // The union across lines is deliberate: the allergy screen is per-beneficiary, and an allergen
            // this platform cannot map is unmapped whichever medicine it was asked about.
            var unmapped = new SortedSet<string>(StringComparer.Ordinal);
            var unresolvable = new HashSet<Guid>();
            int? screened = null;

            if (allergenIds.Length > 0)
            {
                foreach (var drugId in drugIds.Distinct())
                {
                    var check = await PostAsync<AllergyCheckDto>(
                        "masterdata", "/api/v1/allergies/check-by-ids",
                        new { drugId, allergenIds }, bearerToken, token);
                    if (check is null) continue;

                    if (check.Conflict)
                    {
                        conflicts.Add(new AllergyConflict(
                            drugId,
                            check.MatchedOn ?? "conflicts with a recorded allergy",
                            SeverityOf(check.Confidence),
                            check.Confidence,
                            check.StatementEn, check.StatementAr, check.Citation));
                    }

                    if (!check.DrugResolvable) unresolvable.Add(drugId);

                    foreach (var name in check.UnmappedAllergens ?? []) unmapped.Add(name);

                    // The weakest answer across the lines. One medicine screened against two of three
                    // allergies and another against three of three does not make the prescription screened
                    // against three — the check reports per line, and the honest floor is the minimum.
                    //
                    // Nullable rather than seeded at zero, so a first line reporting 0 is a real minimum
                    // rather than an "unset" that the next line's 3 would overwrite.
                    screened = screened is { } s ? Math.Min(s, check.ScreenedAllergenCount) : check.ScreenedAllergenCount;
                }
            }

            return (new AllergyScreen(conflicts, allergenIds.Length, [.. unmapped], screened ?? 0, unresolvable),
                new ProvenanceInfo("EMR allergy record + Mersal allergen catalogue", "live", clock.GetUtcNow()));
        }, ct);

    /// <summary>
    /// How hard an allergy finding should push, given how good the evidence for the match is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A null confidence means the medicine matched the recorded allergy DIRECTLY — the molecule itself, or
    /// the ATC class the allergy covers. That is the allergy, not an inference from it, so it is Major:
    /// interruptive and gating.
    /// </para>
    /// <para>
    /// A cross-reaction is an inference, and design 44 §8 is explicit that a weak one must not be dressed as
    /// a strong one. The harm of over-warning here is concrete rather than theoretical: the often-quoted 10%
    /// penicillin/cephalosporin cross-reactivity figure is not supported by current evidence, and blanket
    /// cephalosporin avoidance after a penicillin label means treating a refugee clinic's patients with a
    /// worse antibiotic. So Low and Theoretical never interrupt.
    /// </para>
    /// </remarks>
    private static ClinicalSeverity SeverityOf(string? confidence) => confidence switch
    {
        null => ClinicalSeverity.Major,
        "High" => ClinicalSeverity.Major,
        "Moderate" => ClinicalSeverity.Moderate,
        "Low" => ClinicalSeverity.Moderate,
        "Theoretical" => ClinicalSeverity.Minor,
        // An unrecognised confidence is a masterdata the port does not understand. Erring towards the
        // interruptive side is the safe direction to be wrong in for an allergy.
        _ => ClinicalSeverity.Major,
    };

    /// <summary>
    /// The dosing rule that applies to THIS patient, for THIS indication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method used to be <c>_ = drugIds; _ = ct;</c> returning an empty dictionary with the provenance
    /// "not-yet-configured" — honest, and the reason the dose check reported "no dosing rule configured" on
    /// every line ever prescribed.
    /// </para>
    /// <para>
    /// Sequenced after the diagnoses and the patient for the same reason as the contraindication fetch: the
    /// applicable rule depends on the indication, the age band and the weight, and asking for them twice
    /// would let the dose check reason about a different patient from the one the other checks saw.
    /// </para>
    /// </remarks>
    private async Task<Fetched<IReadOnlyDictionary<Guid, DosingRuleFact>>> FetchDosingRulesAsync(
        IReadOnlyList<Guid> drugIds, Fetched<DiagnosisContext> diagnoses, Fetched<PatientContext> patient,
        string? bearerToken, CancellationToken ct)
        => await GuardedAsync<IReadOnlyDictionary<Guid, DosingRuleFact>>("dosing rules", async token =>
        {
            var codes = diagnoses is Fetched<DiagnosisContext>.Available d ? d.Value.IcdCodes : [];
            var context = patient is Fetched<PatientContext>.Available p ? p.Value : new PatientContext();

            var body = await PostAsync<DosingRulesDto>(
                "masterdata", "/api/v1/dosing-rules/by-ids",
                new
                {
                    drugIds,
                    diagnosisIcdCodes = codes,
                    population = PopulationOf(context),
                    route = (string?)null,
                    weightKg = context.WeightKg,
                },
                bearerToken, token);

            var facts = (body?.Items ?? []).ToDictionary(
                i => i.DrugId,
                i => new DosingRuleFact(
                    i.DrugId, i.MaxDailyDose, i.DoseUnit, i.MaxDurationDays,
                    i.IsWeightBased, i.RequiresRenalFunction, i.TypicalDailyDose, i.Population, i.Citation));

            return ((IReadOnlyDictionary<Guid, DosingRuleFact>)facts, new ProvenanceInfo(
                "Mersal dosing rules", "curated", clock.GetUtcNow(),
                Caveat: $"Checked against Mersal's own dosing rules ({body?.KnownRuleCount ?? 0} rules); "
                        + "coverage is partial. Outside it the manufacturer's labelled dosing is shown for "
                        + "reference and is NOT compared with what was prescribed."));
        }, ct);

    /// <summary>
    /// The patient's age band, or null when no age is recorded.
    /// </summary>
    /// <remarks>
    /// Null is load-bearing: masterdata then selects no population-specific rule at all, and the dose check
    /// reports what it could not evaluate. Defaulting an unknown age to Adult would apply an adult ceiling to
    /// a four-year-old, which is not a conservative check — it is the absence of one wearing its clothes.
    /// The bands follow the sources the rules are cited from (BNF / BNFC).
    /// </remarks>
    private static string? PopulationOf(PatientContext patient) => patient.AgeYears switch
    {
        null => null,
        < 1 when patient.AgeMonths is < 1 => "Neonate",
        < 2 => "Infant",
        < 12 => "Child",
        < 18 => "Adolescent",
        >= 65 => "Geriatric",
        _ => "Adult",
    };

    /// <summary>
    /// Runs a fetch under a deadline and converts <b>every</b> failure into <c>Unavailable</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately catches broadly. Narrowing it to <c>HttpRequestException</c> is what produced the
    /// original defect — a JSON parse failure or a cancellation would then propagate as an exception, and
    /// the natural fix under pressure is another catch that returns "no alerts".
    /// </remarks>
    private static async Task<Fetched<T>> GuardedAsync<T>(
        string sourceName, Func<CancellationToken, Task<(T Value, ProvenanceInfo Provenance)>> fetch,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerSourceTimeout);

        try
        {
            var (value, provenance) = await fetch(timeout.Token);
            return Fetched.From(value, provenance);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fetched.NotAvailable<T>($"the {sourceName} did not respond within {PerSourceTimeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            return Fetched.NotAvailable<T>($"the {sourceName} could not be reached ({ex.StatusCode?.ToString() ?? "no response"})");
        }
        catch (JsonException)
        {
            return Fetched.NotAvailable<T>($"the {sourceName} returned an unreadable response");
        }
        catch (InvalidOperationException)
        {
            return Fetched.NotAvailable<T>($"the {sourceName} could not be read");
        }
    }

    /// <summary>Throws on a non-2xx, so the guard converts it to Unavailable instead of an empty result.</summary>
    private async Task<T?> GetAsync<T>(string client, string url, string? bearerToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Apply(req, bearerToken);
        using var resp = await factory.CreateClient(client).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    /// <summary>Throws on a non-2xx, so the guard converts it to Unavailable instead of an empty result.</summary>
    private async Task<T?> PostAsync<T>(string client, string url, object body, string? bearerToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        Apply(req, bearerToken);
        using var resp = await factory.CreateClient(client).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static void Apply(HttpRequestMessage req, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return;
        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..] : bearerToken;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record IndicationsDto(IndicationItemDto[]? Items, string? Source, string? SourceRelease);
    private sealed record IndicationItemDto(Guid DrugId, string[]? IcdCategories);
    private sealed record InteractionsDto(
        InteractionItemDto[]? Interactions, int? KnownPairCount, string? SourceRelease,
        DateTimeOffset? RulesUpdatedAt, Guid[]? UnresolvableDrugIds);

    private sealed record InteractionItemDto(
        string? Severity, Guid DrugAId, Guid DrugBId, string? Description, string? MatchedOn,
        string? MechanismEn, string? MechanismAr, string? ClinicalEffectEn, string? ClinicalEffectAr,
        string? ManagementEn, string? ManagementAr, string? Onset, string? EvidenceLevel, string? Citation);
    private sealed record AllergyDto(Guid AllergenId);
    /// <summary>
    /// masterdata's allergy verdict.
    /// </summary>
    /// <remarks>
    /// <c>ScreenedAllergenCount</c> and <c>UnmappedAllergens</c> are not optional detail. Without them a
    /// caller cannot distinguish "compared against all three recorded allergies and found nothing" from
    /// "compared against none of them", and the engine's five-state model exists precisely to keep those
    /// two apart. An older masterdata that omits them deserialises to 0 and null — which reads as "nothing
    /// was screened", the safe direction to be wrong in.
    /// </remarks>
    /// <param name="DrugResolvable">
    /// False when the medicine has neither a recorded ingredient nor an ATC code. Defaults to false on an
    /// older masterdata that omits the field, which reads as "nothing could be compared" — the safe
    /// direction to be wrong in.
    /// </param>
    private sealed record AllergyCheckDto(
        bool Conflict, string? MatchedOn, int ScreenedAllergenCount, string[]? UnmappedAllergens,
        string? MatchKind, string? Confidence, string? StatementEn, string? StatementAr, string? Citation,
        bool DrugResolvable);
    private sealed record ValidationContextDto(
        DiagnosisItemDto[]? Diagnoses, decimal? WeightKg, string? WeightUnit, DateTimeOffset? WeightMeasuredAt);
    private sealed record DiagnosisItemDto(string IcdCode, string? Rank);
    private sealed record BeneficiaryDto(DateTimeOffset? DateOfBirth);
    private sealed record DosingRulesDto(DosingRuleItemDto[]? Items, int? KnownRuleCount);
    private sealed record DosingRuleItemDto(
        Guid DrugId, decimal? MaxDailyDose, string? DoseUnit, int? MaxDurationDays,
        bool IsWeightBased, bool RequiresRenalFunction, decimal? TypicalDailyDose,
        string? Population, string? Citation);

    private sealed record ContraindicationsDto(ContraindicationItemDto[]? Items, int? KnownRuleCount);
    private sealed record ContraindicationItemDto(
        Guid DrugId, string? Severity, string? MatchedOn, string? IcdScope,
        string? MechanismEn, string? MechanismAr, string? ClinicalEffectEn, string? ClinicalEffectAr,
        string? ManagementEn, string? ManagementAr, string? EvidenceLevel, string? Citation);
    private sealed record IcdAncestorsDto(IcdAncestorItemDto[]? Items);
    private sealed record IcdAncestorItemDto(string Code, string[]? Ancestors);
    private sealed record IngredientsDto(IngredientItemDto[]? Items);
    private sealed record IngredientItemDto(
        Guid DrugId, string? Name, string? ScientificName, string? AtcCode, string[]? IngredientKeys);
}
