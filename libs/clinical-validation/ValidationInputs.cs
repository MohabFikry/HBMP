namespace Mersal.ClinicalValidation;

/// <summary>One line of the prescription being written.</summary>
/// <param name="DurationDays">
/// New in phase 26 — the schema had dose, route, frequency and quantity but no duration, and duration is
/// what makes a daily-dose ceiling or a treatment-length limit checkable at all.
/// </param>
public sealed record PrescriptionLineInput(
    Guid LineId,
    Guid DrugId,
    string DrugName,
    string? DoseText = null,
    decimal? DoseAmount = null,
    string? DoseUnit = null,
    int? TimesPerDay = null,
    int? DurationDays = null,
    int? Quantity = null);

/// <summary>
/// What is being validated: the lines, and which encounter they belong to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The diagnoses are deliberately NOT here.</b> They used to be, and that is exactly how the platform's
/// most careful path came to trust its client for its most important input: step 2 re-ran every check
/// server-side and then read <c>req.DiagnosisIcdCodes</c> straight out of the request body (doc 44 §1.3).
/// A submission with an emptied or edited diagnosis array changed what the engine concluded.
/// </para>
/// <para>
/// Diagnoses are FETCHED DATA, so they live in <see cref="ValidationSnapshot"/> behind
/// <see cref="Fetched{T}"/> like every other fetched fact. Phase 26 proved that an invariant carried by the
/// type system survives and one carried by review does not — moving the field is what makes "the server
/// reads the diagnosis from the encounter" structural rather than a rule someone has to remember.
/// </para>
/// </remarks>
/// <param name="ActiveMedicationDrugIds">
/// The beneficiary's current medications, so interactions are checked against what they are already taking
/// and not only across the lines being written now.
/// </param>
public sealed record ValidationRequest(
    Guid EncounterId,
    IReadOnlyList<PrescriptionLineInput> Lines,
    IReadOnlyList<Guid> ActiveMedicationDrugIds);

/// <summary>Where a validation run's diagnosis list came from.</summary>
/// <remarks>
/// Stored with the run so a step-1/step-2 divergence is explainable rather than mysterious. Step 1 runs in
/// the doctor's browser while they compose and may use the list the screen is holding; step 2 is the
/// authority and reads the encounter. When the two disagree, this field is the answer to why.
/// </remarks>
public enum DiagnosisProvenance
{
    /// <summary>Read from the encounter by the server. The only provenance the authoritative path accepts.</summary>
    EncounterFetched,

    /// <summary>Supplied by the client for a step-1 advisory run. Display state, never authority.</summary>
    ClientSupplied,
}

/// <summary>
/// The encounter's recorded diagnoses, as recorded — specific codes such as "E11.9".
/// </summary>
/// <remarks>
/// An EMPTY list is meaningful and is not the same as "no match": the indication check reports "no diagnosis
/// recorded", never Ok (doc 43 §6). And an empty list is not the same as a FAILED fetch either, which is why
/// this travels inside <see cref="Fetched{T}"/> — emr being unreachable makes the check Unavailable, not
/// NotChecked and certainly not Ok.
/// </remarks>
/// <param name="Ancestors">
/// Each diagnosis code's ancestor chain from the loaded ICD-10 hierarchy — subcategory → category → block →
/// chapter (28.7). An EMPTY chain for a code is a real answer, not an error: the catalogue may not have been
/// reloaded since the closure table arrived, and the check falls back to a category comparison rather than
/// reporting a mismatch on every prescription.
/// </param>
public sealed record DiagnosisContext(
    IReadOnlyList<string> IcdCodes,
    DiagnosisProvenance Source,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Ancestors = null);

/// <summary>
/// What a prescribed product is made of, for the checks that reason about molecules.
/// </summary>
/// <param name="IngredientKeys">
/// The normalised molecule names the product resolves to. A combination product has several; a product
/// whose ingredient text the catalogue never recorded has NONE, and that absence is load-bearing — the
/// duplication check must say it could not compare rather than pass the line.
/// </param>
/// <param name="AtcClass4">
/// The 4-character ATC class ("M01A"), or null. Two medicines in the same class — two NSAIDs, two PPIs, two
/// SSRIs — are a duplication even when the molecules differ.
/// </param>
public sealed record DrugComposition(
    Guid DrugId,
    IReadOnlyList<string> IngredientKeys,
    string? AtcClass4);

/// <summary>A drug's listed indications, as ICD categories.</summary>
/// <param name="IcdCategories">
/// May be empty while the drug still exists in the catalogue — 1,019 products are in exactly that state.
/// Empty means "not checked", never "no match".
/// </param>
/// <param name="NodeAncestors">
/// Each indication node's own ancestor chain, used only for the INVERSE case: a diagnosis recorded less
/// specifically than the indication ("E11" against an "E11.9" indication). Design 44 §6 requires that to be
/// reported as a possible match at lower confidence — the patient may well have the more specific condition,
/// but nobody has coded it that way, so calling it a hit asserts something the record does not say and
/// calling it a miss warns off-label on a prescription that is very likely on-label.
/// </param>
public sealed record DrugIndicationFact(
    Guid DrugId,
    IReadOnlyList<string> IcdCategories,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? NodeAncestors = null);

/// <summary>
/// One curated interaction that fired between two prescribed products.
/// </summary>
/// <remarks>
/// <para>
/// The pair is reported by DRUG so the engine can attach it to the right lines, but it was MATCHED on
/// molecules and ATC classes (28.3). The old model keyed the rule itself on two product uuids, which needed
/// one row per pair of brands and so held zero rows for ever.
/// </para>
/// <para>
/// Mechanism, effect and management are not decoration (design 44 §3). "Major interaction with
/// clarithromycin" is not something a prescriber can act on; "CYP3A4 inhibition → 10-fold simvastatin
/// exposure → rhabdomyolysis; suspend the statin for the antibiotic course" is. Management is the field most
/// likely to change the prescription, so it is rendered rather than tucked into a disclosure.
/// </para>
/// </remarks>
public sealed record InteractionFact(
    Guid DrugAId,
    Guid DrugBId,
    ClinicalSeverity Severity,
    string? Description,
    string? MatchedOn = null,
    string? MechanismEn = null,
    string? MechanismAr = null,
    string? ClinicalEffectEn = null,
    string? ClinicalEffectAr = null,
    string? ManagementEn = null,
    string? ManagementAr = null,
    string? Onset = null,
    string? EvidenceLevel = null,
    string? Citation = null);

/// <summary>
/// The interaction data for this validation.
/// </summary>
/// <param name="KnownPairCount">
/// How many pairs the source holds in total. This is the difference between "no interaction between these
/// drugs" and "the list is empty so nothing could have been found" — the second must report NotChecked.
/// Doc 43 D3 is explicit that internal curation means partial coverage and that the UI must say so rather
/// than imply completeness.
/// </param>
/// <param name="RulesUpdatedAt">
/// When the list last changed. Shown with the pair count so the UI can state coverage honestly — "checked
/// against Mersal's interaction list (13 ingredient pairs, updated 6 Aug 2026)". A partial list is ethical
/// to ship only when its partiality is visible (doc 44 §8).
/// </param>
/// <param name="UnresolvableDrugIds">
/// Products with neither a recorded molecule nor an ATC code, which no rule can match. Reported so the
/// engine names the medicine it could not check instead of implying the prescription was screened.
/// </param>
public sealed record InteractionTable(
    IReadOnlyList<InteractionFact> Pairs,
    int KnownPairCount,
    DateTimeOffset? RulesUpdatedAt = null,
    IReadOnlySet<Guid>? UnresolvableDrugIds = null);

/// <summary>
/// A drug that conflicts with a recorded allergy, and everything the prescriber needs to act on it.
/// </summary>
/// <param name="MatchedOn">
/// What matched, in words: "contains amoxicillin, which is covered by the recorded allergy to Penicillins",
/// not "atc-class". Design 44 §3 — an alert that names no mechanism is one a prescriber cannot act on.
/// </param>
/// <param name="Confidence">
/// Set only for a cross-reactivity match. Null means the match IS the recorded allergy rather than an
/// inference from it, and the two are worded and weighted differently.
/// </param>
/// <param name="ManagementEn">
/// What to do instead — the field most likely to change the prescription. "Consider a cephalosporin with a
/// different side chain" converts an obstacle into advice.
/// </param>
public sealed record AllergyConflict(
    Guid DrugId,
    string MatchedOn,
    ClinicalSeverity Severity = ClinicalSeverity.Major,
    string? Confidence = null,
    string? ManagementEn = null,
    string? ManagementAr = null,
    string? Citation = null);

/// <summary>
/// The allergy data for this validation.
/// </summary>
/// <remarks>
/// <para>
/// The counts are the load-bearing part, not the conflicts. Phase 26 carried only
/// <paramref name="RecordedAllergenCount"/>, so the check could distinguish "no history recorded" from
/// "history recorded" and nothing else — in particular it could not tell <b>screened and clean</b> from
/// <b>never actually compared</b>. masterdata's matcher was structurally incapable of ever matching
/// (doc 44 §1.1), and this type had no way to express that, so the engine reported Ok.
/// </para>
/// <para>
/// Three numbers now describe the screen, and the gaps between them are what the finding reports.
/// </para>
/// </remarks>
/// <param name="RecordedAllergenCount">
/// How many allergies the beneficiary has recorded, of any category. Zero means the check could not run —
/// an absent allergy history is not a negative allergy history.
/// </param>
/// <param name="UnmappedAllergens">
/// Display names of recorded <b>drug</b> allergens that could not be resolved to an ingredient, an ATC
/// scope or a cross-reactivity group. A gap in our own catalogue, and the check must name it: "not
/// checked" without a subject gives the prescriber nothing to act on and nothing to correct.
/// </param>
/// <param name="ScreenedAllergenCount">
/// How many recorded allergens were actually compared against this medicine. Lower than
/// <paramref name="RecordedAllergenCount"/> whenever an allergen is unmapped, and also whenever one is
/// simply not a question about a medicine — a peanut allergy is neither screened nor a data gap.
/// <b>Only a screen where this equals the recorded count and nothing is unmapped may report Ok.</b>
/// </param>
/// <param name="UnresolvableDrugIds">
/// Medicines with neither a recorded active ingredient nor an ATC code, so nothing could be compared against
/// them. A gap in the DRUG's catalogue row rather than in the patient's allergy record — 4.7% of products
/// have no ingredient text and 14.8% no ATC — and the prescriber must not be told their patient's allergy
/// history is at fault when the catalogue is.
/// </param>
public sealed record AllergyScreen(
    IReadOnlyList<AllergyConflict> Conflicts,
    int RecordedAllergenCount,
    IReadOnlyList<string> UnmappedAllergens,
    int ScreenedAllergenCount,
    IReadOnlySet<Guid>? UnresolvableDrugIds = null);

/// <summary>Whether renal function is known, and if so what it is.</summary>
/// <remarks>
/// <b>Always Unknown today, and deliberately modelled rather than omitted.</b> Lab results are stored as
/// <c>result_value text</c> (orders 0002_fulfillment.sql), so the platform holds no structured creatinine or
/// eGFR and computing a renal adjustment is impossible. Design 44 §4 is explicit about the consequence: a
/// dose check that silently ignores renal function on a renally-cleared drug in a patient with kidney
/// disease is worse than no check, because it reassures. The slot exists so the check can SAY that.
/// </remarks>
public enum RenalFunction { Unknown }

/// <summary>Whether the patient is pregnant. A status, not a lab (design 44 §5).</summary>
public enum PregnancyStatus
{
    /// <summary>Nobody has recorded it, or what was recorded is too old to be current.</summary>
    Unknown,

    Pregnant,
    NotPregnant,
}

/// <summary>
/// What the engine knows about the patient — age, weight, renal function, pregnancy.
/// </summary>
/// <remarks>
/// <para>
/// Phase 26's <c>ValidationRequest</c> carried the encounter, the lines, the diagnoses and the active
/// medications, and <b>nothing about the patient</b>. A dose check without an age or a weight is an adult
/// fixed-dose check, and this population skews paediatric.
/// </para>
/// <para>
/// Fetched server-side alongside the diagnoses (28.2), for the same reason and through the same call.
/// </para>
/// </remarks>
/// <param name="WeightMeasuredAt">
/// When the weight was taken. NOT decoration: a two-year-old weight on a growing child is worse than none,
/// so a weight older than the applicable window is treated as a MISSING input rather than a current fact.
/// Sending the value without its date would make that judgement impossible.
/// </param>
public sealed record PatientContext(
    int? AgeYears = null,
    int? AgeMonths = null,
    decimal? WeightKg = null,
    DateTimeOffset? WeightMeasuredAt = null,
    RenalFunction Renal = RenalFunction.Unknown,
    PregnancyStatus Pregnancy = PregnancyStatus.Unknown)
{
    /// <summary>Under 18, where an age is known at all.</summary>
    public bool IsChild => AgeYears is { } y && y < 18;

    /// <summary>
    /// Whether the recorded weight is recent enough to dose against, given the patient's age.
    /// </summary>
    /// <remarks>
    /// 30 days for a child, 90 for an adult. A child's weight changes fast enough that a two-month-old
    /// figure is a different patient; an adult's does not. The windows are the design's defaults (§4) and
    /// are deliberately conservative — erring towards "not checked, weight needed" costs a measurement,
    /// while erring the other way costs a mg/kg calculation against a number that is no longer true.
    /// </remarks>
    public bool HasCurrentWeight(DateTimeOffset now) =>
        WeightKg is > 0 && WeightMeasuredAt is { } at
        && now - at <= TimeSpan.FromDays(IsChild ? 30 : 90);
}

/// <summary>
/// One condition-based contraindication that fired for one prescribed medicine (28.9, doc 44 §5).
/// </summary>
/// <param name="MatchedOn">
/// The drug side and the condition side in words — "is in ATC class M01A, with the recorded diagnosis
/// K27.9". A prescriber shown only "contraindicated" cannot carry the reasoning to the next drug.
/// </param>
/// <param name="ManagementEn">
/// What to give instead. A contraindication that says only "avoid" leaves the prescriber with a patient
/// still in pain and no alternative, which is how a safety rule becomes something to click past.
/// </param>
public sealed record ContraindicationFact(
    Guid DrugId,
    string MatchedOn,
    ClinicalSeverity Severity,
    string MechanismEn,
    string MechanismAr,
    string ClinicalEffectEn,
    string ClinicalEffectAr,
    string ManagementEn,
    string ManagementAr,
    string? Citation = null);

/// <param name="KnownRuleCount">
/// How many contraindication rules the list holds. Zero means nothing could have been found, which reports
/// NotChecked — the same coverage honesty the interaction list carries.
/// </param>
public sealed record ContraindicationTable(
    IReadOnlyList<ContraindicationFact> Hits,
    int KnownRuleCount);

/// <summary>
/// A structured dosing rule for one drug.
/// </summary>
/// <remarks>
/// Deliberately narrow. Doc 43 §2: an automated "is this dose right for this diagnosis" cannot be derived
/// from label prose, so what is defensible is a curated, high-risk subset authored by a pharmacist. Outside
/// that subset the check says "no dosing rule configured" — not silence, and never an implied endorsement.
/// </remarks>
/// <param name="IsWeightBased">
/// mg/kg is the only correct paediatric calculation, and a weight-based rule evaluated without a weight must
/// report the missing input rather than pass (28.8).
/// </param>
/// <param name="RequiresRenalFunction">
/// The drug is renally cleared. Always yields NotChecked today: the platform stores laboratory results as
/// free text, so there is no structured eGFR to adjust against, and saying so is the honest answer.
/// </param>
/// <param name="Citation">
/// Where the numbers came from. A recommended range shown without its source is a number the platform cannot
/// defend, and a prescriber is entitled to weigh it.
/// </param>
public sealed record DosingRuleFact(
    Guid DrugId,
    decimal? MaxDailyDose = null,
    string? DoseUnit = null,
    int? MaxDurationDays = null,
    bool IsWeightBased = false,
    bool RequiresRenalFunction = false,
    decimal? TypicalDailyDose = null,
    string? Population = null,
    string? Citation = null)
{
    /// <summary>The recommended range as a sentence, or empty when the rule carries no numbers to show.</summary>
    public string RecommendedRange()
    {
        if (MaxDailyDose is not { } max) return "";

        var population = string.IsNullOrWhiteSpace(Population) ? "" : $" for {Population}";
        var typical = TypicalDailyDose is { } t ? $"typically {t}{DoseUnit}, " : "";
        var source = string.IsNullOrWhiteSpace(Citation) ? "" : $" Source: {Citation}.";

        return $" Recommended{population}: {typical}maximum {max}{DoseUnit} daily.{source}";
    }
}

/// <summary>
/// A benefit verdict for one line, produced by the seam that phase 27 implements for real.
/// </summary>
public sealed record BenefitOutcome(
    Guid LineId,
    BenefitState State,
    string MessageEn,
    string MessageAr,
    ProvenanceInfo? Provenance = null);

/// <summary>
/// Everything the validator needs, already fetched. The validator itself performs no I/O.
/// </summary>
/// <remarks>
/// Each field is a <see cref="Fetched{T}"/>, so "we could not reach this source" is carried in the data
/// rather than signalled by an empty collection. That is what makes it impossible for a failed fetch to be
/// mistaken for a clean result.
/// </remarks>
/// <param name="Labels">
/// Manufacturer labels retrieved live from openFDA, keyed by drug.
/// <para>
/// A <b>second, independent</b> interaction and dosing source, not a replacement for the curated ones. The
/// local interaction list holds zero pairs today, so it reports "not checked" on every line; label text is
/// the only thing currently capable of raising a drug-drug interaction at all. It is also the weaker source
/// — prose written per product rather than a curated matrix — so it may warn but is never permitted to
/// reassure.
/// </para>
/// </param>
/// <param name="Diagnoses">
/// The encounter's diagnoses, with their provenance. Fetched from emr on the authoritative path (28.2) —
/// never taken from the request body, which is where the trusted-client hole was.
/// </param>
public sealed record ValidationSnapshot(
    Fetched<IReadOnlyDictionary<Guid, DrugIndicationFact>> Indications,
    Fetched<InteractionTable> Interactions,
    Fetched<AllergyScreen> Allergies,
    Fetched<IReadOnlyDictionary<Guid, DosingRuleFact>> DosingRules,
    Fetched<IReadOnlyList<BenefitOutcome>> Benefit,
    Fetched<LabelEvidence> Labels,
    Fetched<DiagnosisContext> Diagnoses,
    Fetched<IReadOnlyDictionary<Guid, DrugComposition>> Compositions,
    Fetched<PatientContext> Patient,
    Fetched<ContraindicationTable> Contraindications,
    // 29.6 (design 45 §6) — the drug's pack facts, behind Fetched like every other fetched fact, so
    // "masterdata was unreachable" stays distinguishable from "master data does not record this".
    Fetched<IReadOnlyDictionary<Guid, DrugPackFacts>> PackFacts);

/// <summary>
/// 29.6 — what the drug master records about how a product is packed (design 45 §6).
/// </summary>
/// <param name="IsPackSplittable">
/// <b>Null means the master data does not say</b>, and that is not the same as false. Defaulting it either
/// way is the error invariant 8 exists to prevent: assuming splittable permits a fractional inhaler, and
/// assuming not would round a tablet count up to whole boxes.
/// </param>
/// <param name="PackSize">The catalogue's own count for one pack, as recorded. Null ⇒ not recorded.</param>
/// <param name="PackContent">
/// 31.3 — how many PRESCRIBING units one box holds, which is what a course is divided by. The same number as
/// <paramref name="PackSize"/> for tablets and capsules; 120 rather than 1 for a 120 ml bottle of syrup, and
/// 1500 rather than 5 for a box of five insulin pens. Null ⇒ the catalogue records nothing to derive it from,
/// and the check then reports NotChecked naming this column.
/// </param>
public sealed record DrugPackFacts(bool? IsPackSplittable, decimal? PackSize, decimal? PackContent = null);
