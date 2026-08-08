namespace Mersal.ClinicalValidation.Tests;

/// <summary>Builders for validation inputs, so each test states only what it is about.</summary>
internal static class Fx
{
    public static readonly DateTimeOffset RanAt = new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

    public static readonly ProvenanceInfo Provenance = new("Mersal test source", "R-test", RanAt);

    /// <summary>Distinct from <see cref="Provenance"/> so a test can tell the two interaction sources apart.</summary>
    public static readonly ProvenanceInfo LabelProvenance = new("openFDA test label", "live", RanAt);

    public static PrescriptionLineInput Line(
        Guid? lineId = null, Guid? drugId = null, string name = "Test drug",
        decimal? doseAmount = null, string? doseUnit = null, int? timesPerDay = null,
        int? durationDays = null) =>
        new(lineId ?? Guid.NewGuid(), drugId ?? Guid.NewGuid(), name,
            DoseAmount: doseAmount, DoseUnit: doseUnit, TimesPerDay: timesPerDay, DurationDays: durationDays);

    /// <summary>
    /// The lines being written, and nothing about the diagnosis.
    /// </summary>
    /// <remarks>
    /// There is deliberately no <c>diagnoses</c> parameter. It used to be here, mirroring the production
    /// record, and 28.2 moved diagnoses onto the SNAPSHOT because they are fetched data — see the note on
    /// <see cref="ValidationRequest"/>. Leaving an ignored parameter behind would let a test say
    /// <c>diagnoses: ["E11.9"]</c>, pass, and prove nothing.
    /// </remarks>
    public static ValidationRequest Request(
        IEnumerable<PrescriptionLineInput>? lines = null,
        IEnumerable<Guid>? activeMedications = null) =>
        new(Guid.NewGuid(), [.. lines ?? [Line()]], [.. activeMedications ?? []]);

    /// <summary>
    /// The encounter's diagnoses as the SERVER read them.
    /// </summary>
    /// <remarks>
    /// They live on the snapshot rather than the request because they are fetched data — see the note on
    /// <see cref="ValidationRequest"/>. A test that passes <c>diagnoses:</c> to <see cref="Request"/> is
    /// really describing the snapshot, so <see cref="Snapshot"/> takes the same argument and builds this.
    /// </remarks>
    public static Fetched<DiagnosisContext> Diagnoses(
        IEnumerable<string>? codes = null,
        DiagnosisProvenance source = DiagnosisProvenance.EncounterFetched,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ancestors = null) =>
        Fetched.From(new DiagnosisContext([.. codes ?? []], source, ancestors), Provenance);

    /// <summary>An ICD ancestor chain, as the loaded hierarchy would supply it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Hierarchy(
        params (string Code, string[] Ancestors)[] rows) =>
        rows.ToDictionary(r => r.Code, r => (IReadOnlyList<string>)r.Ancestors, StringComparer.Ordinal);

    // ---- snapshot pieces, each defaulting to "available and usable" -----------------------------------

    public static Fetched<IReadOnlyDictionary<Guid, DrugIndicationFact>> Indications(
        params DrugIndicationFact[] facts) =>
        Fetched.From<IReadOnlyDictionary<Guid, DrugIndicationFact>>(
            facts.ToDictionary(f => f.DrugId), Provenance);

    public static Fetched<InteractionTable> Interactions(int knownPairCount, params InteractionFact[] pairs) =>
        Fetched.From(new InteractionTable(pairs, knownPairCount), Provenance);

    /// <summary>
    /// A completed allergy screen.
    /// </summary>
    /// <param name="recordedCount">How many allergies the beneficiary has recorded, of any category.</param>
    /// <param name="screenedCount">
    /// How many of them were actually compared against the medicine. Defaults to <paramref name="recordedCount"/>
    /// — "everything recorded was screened" — so a test that does not mention mapping is testing the fully
    /// mapped case, which is the only one entitled to report Ok.
    /// </param>
    /// <param name="unmapped">
    /// Recorded drug allergens with no mapping to an ingredient, ATC scope or cross-reactivity group. Naming
    /// them is the whole point: the check has to say what it could not evaluate.
    /// </param>
    public static Fetched<AllergyScreen> Allergies(
        int recordedCount, int? screenedCount = null, string[]? unmapped = null,
        params AllergyConflict[] conflicts) =>
        Fetched.From(
            new AllergyScreen(conflicts, recordedCount, unmapped ?? [], screenedCount ?? recordedCount),
            Provenance);

    public static Fetched<IReadOnlyDictionary<Guid, DosingRuleFact>> DosingRules(params DosingRuleFact[] rules) =>
        Fetched.From<IReadOnlyDictionary<Guid, DosingRuleFact>>(rules.ToDictionary(r => r.DrugId), Provenance);

    public static Fetched<IReadOnlyList<BenefitOutcome>> Benefit(params BenefitOutcome[] outcomes) =>
        Fetched.From<IReadOnlyList<BenefitOutcome>>(outcomes, Provenance);

    /// <summary>A retrieved manufacturer label. <paramref name="interactions"/> is the prose to scan.</summary>
    public static DrugLabelFact Label(
        Guid drugId, string genericName, string? interactions = null, string? dosing = null,
        string? strengths = null, params string[] aliases) =>
        new(drugId, genericName, genericName.ToUpperInvariant(),
            aliases.Length > 0 ? [genericName, .. aliases] : [genericName],
            interactions, dosing, strengths, "20260101");

    public static Fetched<LabelEvidence> Labels(params DrugLabelFact[] facts) =>
        Fetched.From(
            new LabelEvidence(facts.ToDictionary(f => f.DrugId), new Dictionary<Guid, string>(),
                new Dictionary<Guid, string>()),
            LabelProvenance);

    /// <summary>Labels where a drug could not be matched — an answer, distinct from a source failure.</summary>
    public static Fetched<LabelEvidence> LabelsUnmatched(Guid drugId, string reason, params DrugLabelFact[] found) =>
        Fetched.From(
            new LabelEvidence(found.ToDictionary(f => f.DrugId),
                new Dictionary<Guid, string> { [drugId] = reason }, new Dictionary<Guid, string>()),
            LabelProvenance);

    /// <summary>Labels where a lookup FAILED — must never render as "nothing found".</summary>
    public static Fetched<LabelEvidence> LabelsFailed(Guid drugId, string reason) =>
        Fetched.From(
            new LabelEvidence(new Dictionary<Guid, DrugLabelFact>(), new Dictionary<Guid, string>(),
                new Dictionary<Guid, string> { [drugId] = reason }),
            LabelProvenance);

    /// <summary>What each prescribed product is made of, for the duplicate-therapy check.</summary>
    public static Fetched<IReadOnlyDictionary<Guid, DrugComposition>> Compositions(
        params DrugComposition[] compositions) =>
        Fetched.From<IReadOnlyDictionary<Guid, DrugComposition>>(
            compositions.ToDictionary(c => c.DrugId), Provenance);

    /// <summary>
    /// The patient the prescription is for. Defaults to an adult weighed today, so a test that does not
    /// mention weight is testing the case where a weight-based rule CAN be evaluated.
    /// </summary>
    public static Fetched<PatientContext> Patient(
        int? ageYears = 40, decimal? weightKg = 70, DateTimeOffset? weighedAt = null,
        PregnancyStatus pregnancy = PregnancyStatus.Unknown) =>
        Fetched.From(
            new PatientContext(ageYears, null, weightKg, weightKg is null ? null : weighedAt ?? RanAt,
                RenalFunction.Unknown, pregnancy),
            Provenance);

    /// <summary>The contraindication rules that fired. Defaults to a populated list that found nothing.</summary>
    public static Fetched<ContraindicationTable> Contraindications(
        int knownRuleCount = 8, params ContraindicationFact[] hits) =>
        Fetched.From(new ContraindicationTable(hits, knownRuleCount), Provenance);

    /// <summary>A snapshot where every source is reachable. Override only the part under test.</summary>
    public static ValidationSnapshot Snapshot(
        Fetched<IReadOnlyDictionary<Guid, DrugIndicationFact>>? indications = null,
        Fetched<InteractionTable>? interactions = null,
        Fetched<AllergyScreen>? allergies = null,
        Fetched<IReadOnlyDictionary<Guid, DosingRuleFact>>? dosingRules = null,
        Fetched<IReadOnlyList<BenefitOutcome>>? benefit = null,
        Fetched<LabelEvidence>? labels = null,
        Fetched<DiagnosisContext>? diagnoses = null,
        Fetched<IReadOnlyDictionary<Guid, DrugComposition>>? compositions = null,
        Fetched<PatientContext>? patient = null,
        Fetched<ContraindicationTable>? contraindications = null,
        Fetched<IReadOnlyDictionary<Guid, DrugPackFacts>>? packFacts = null) =>
        new(indications ?? Indications(),
            interactions ?? Interactions(knownPairCount: 100),
            allergies ?? Allergies(recordedCount: 1),
            dosingRules ?? DosingRules(),
            benefit ?? Benefit(),
            labels ?? Labels(),
            diagnoses ?? Diagnoses(),
            compositions ?? Compositions(),
            patient ?? Patient(),
            contraindications ?? Contraindications(),
            // 29.6 — the default is an EMPTY-but-available map, so a test that says nothing about pack facts
            // gets the honest "master data does not record this" rather than a fabricated pack.
            packFacts ?? PackFacts());

    /// <summary>Every source down — the outage case.</summary>
    public static ValidationSnapshot DeadSnapshot(string reason = "masterdata unreachable") =>
        new(Fetched.NotAvailable<IReadOnlyDictionary<Guid, DrugIndicationFact>>(reason),
            Fetched.NotAvailable<InteractionTable>(reason),
            Fetched.NotAvailable<AllergyScreen>(reason),
            Fetched.NotAvailable<IReadOnlyDictionary<Guid, DosingRuleFact>>(reason),
            Fetched.NotAvailable<IReadOnlyList<BenefitOutcome>>(reason),
            Fetched.NotAvailable<LabelEvidence>(reason),
            Fetched.NotAvailable<DiagnosisContext>(reason),
            Fetched.NotAvailable<IReadOnlyDictionary<Guid, DrugComposition>>(reason),
            Fetched.NotAvailable<PatientContext>(reason),
            Fetched.NotAvailable<ContraindicationTable>(reason),
            Fetched.NotAvailable<IReadOnlyDictionary<Guid, DrugPackFacts>>(reason));

    /// <summary>
    /// 29.6 — the drug pack facts, as master data records them (design 45 §6).
    /// </summary>
    /// <remarks>
    /// Called with no arguments this is an EMPTY map that IS available — "the catalogue describes no pack
    /// for this drug", which the quantity check must report as NotChecked. That is a different fact from an
    /// unreachable catalogue, so the empty case deliberately does not use <c>NotAvailable</c>.
    /// </remarks>
    public static Fetched<IReadOnlyDictionary<Guid, DrugPackFacts>> PackFacts(
        params (Guid DrugId, bool? isSplittable, decimal? packSize)[] rows) =>
        Fetched.From<IReadOnlyDictionary<Guid, DrugPackFacts>>(
            rows.ToDictionary(r => r.DrugId, r => new DrugPackFacts(r.isSplittable, r.packSize)),
            Provenance);

    public static ValidationResult Run(ValidationRequest request, ValidationSnapshot snapshot) =>
        PrescriptionValidator.Validate(request, snapshot, RanAt);

    /// <summary>
    /// Runs with the diagnoses a test names, wherever it named them.
    /// </summary>
    /// <remarks>
    /// Diagnoses moved from the request to the snapshot in 28.2. Tests read more naturally saying
    /// <c>Fx.Request([line], diagnoses: ["E11.9"])</c>, so this overload accepts them there and routes them
    /// to the snapshot — the engine only ever sees the fetched copy.
    /// </remarks>
    public static ValidationResult Run(
        ValidationRequest request, ValidationSnapshot snapshot, IEnumerable<string> diagnoses) =>
        PrescriptionValidator.Validate(
            request,
            // A snapshot the test built as DEAD stays dead: overriding its diagnoses with a live fetch would
            // quietly repair the outage the test is about.
            snapshot.Diagnoses is Fetched<DiagnosisContext>.Unavailable
                ? snapshot
                : snapshot with { Diagnoses = Diagnoses(diagnoses) },
            RanAt);

    /// <summary>
    /// The finding from the platform's <b>curated</b> source for a check.
    /// </summary>
    /// <remarks>
    /// Interactions now have two independent sources — the curated pair list and manufacturer label text —
    /// so "the interaction finding" is no longer a single thing and a test has to say which one it means.
    /// Use <see cref="LabelFor"/> for the other.
    /// </remarks>
    public static Finding For(this ValidationResult result, Guid lineId, CheckKind kind) =>
        result.Findings.Single(f => f.LineId == lineId && f.Kind == kind && !IsFromLabel(f));

    /// <summary>The finding produced from manufacturer label text.</summary>
    public static Finding LabelFor(this ValidationResult result, Guid lineId, CheckKind kind) =>
        result.Findings.Single(f => f.LineId == lineId && f.Kind == kind && IsFromLabel(f));

    /// <summary>
    /// Provenance names the source — except when a check is Unavailable, which by design carries no
    /// provenance to attribute, so the label pass's outage finding is recognised by its wording instead.
    /// </summary>
    private static bool IsFromLabel(Finding f) =>
        f.Provenance?.SourceName == LabelProvenance.SourceName
        || f.MessageEn.StartsWith("Manufacturer-label", StringComparison.Ordinal);
}
