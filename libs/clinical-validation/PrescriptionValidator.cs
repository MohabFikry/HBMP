using Mersal.Prescribing;
using Mersal.ClinicalCodes;

namespace Mersal.ClinicalValidation;

/// <summary>
/// Evaluates a prescription against already-fetched clinical and benefit data (phase 26.3, doc 43 §6).
/// </summary>
/// <remarks>
/// <para>
/// Pure. No I/O, no clock of its own, no service calls — the caller fetches, this decides. That is what lets
/// phase 27's step-2 evaluator reuse it verbatim: one implementation of "what does this prescription look
/// like", evaluated once in the doctor's browser as advice and once on the server as the authority.
/// </para>
/// <para>
/// Every checker below is typed to return <see cref="ClinicalState"/>, which has no <c>Blocked</c>. The one
/// exception is the benefit pass, which is the only thing on the platform entitled to refuse.
/// </para>
/// </remarks>
public static class PrescriptionValidator
{
    public static ValidationResult Validate(
        ValidationRequest request, ValidationSnapshot snapshot, DateTimeOffset ranAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);

        var findings = new List<Finding>();

        foreach (var line in request.Lines)
        {
            findings.Add(IndicationChecks.Evaluate(line, snapshot.Diagnoses, snapshot.Indications));
            findings.Add(AllergyChecks.Evaluate(line, snapshot.Allergies));
            findings.Add(DoseChecks.Evaluate(line, snapshot.DosingRules, snapshot.Labels, snapshot.Patient, ranAt));
            findings.Add(ContraindicationChecks.Evaluate(line, snapshot.Contraindications));
            findings.Add(QuantityChecks.Evaluate(line, snapshot.PackFacts));
        }

        findings.AddRange(InteractionChecks.Evaluate(request, snapshot.Interactions, snapshot.ActiveMedications));
        findings.AddRange(DuplicationChecks.Evaluate(request, snapshot.Compositions));

        // A second, independent interaction pass over manufacturer label text. Deliberately additive: the
        // two sources answer with different authority and different provenance, and collapsing them into one
        // verdict would hide which one spoke.
        findings.AddRange(LabelInteractionChecks.Evaluate(request, snapshot.Labels));
        findings.AddRange(BenefitChecks.Evaluate(request, snapshot.Benefit));

        return new ValidationResult(findings, ranAt);
    }
}

internal static class IndicationChecks
{
    internal static Finding Evaluate(
        PrescriptionLineInput line,
        Fetched<DiagnosisContext> diagnosisContext,
        Fetched<IReadOnlyDictionary<Guid, DrugIndicationFact>> indications)
    {
        // THE DIAGNOSIS SOURCE FAILED — a different fact from "no diagnosis was recorded", and it outranks
        // everything below (28.2, doc 44 §1.3). emr being unreachable means this check did not run; saying
        // "no diagnosis recorded" would blame the encounter for the platform's outage, and saying Ok would
        // be the false assurance the whole five-state model exists to prevent.
        if (diagnosisContext is Fetched<DiagnosisContext>.Unavailable du)
        {
            return Clinical(line, ClinicalState.Unavailable,
                $"Indication check unavailable — the encounter's diagnoses could not be read ({du.Reason}).",
                $"تعذّر التحقق من دواعي الاستعمال — تعذّرت قراءة تشخيصات الزيارة ({du.Reason}).",
                provenance: null);
        }

        var diagnosisContextValue = ((Fetched<DiagnosisContext>.Available)diagnosisContext).Value;
        var diagnoses = diagnosisContextValue.IcdCodes;

        if (indications is Fetched<IReadOnlyDictionary<Guid, DrugIndicationFact>>.Unavailable u)
        {
            return Clinical(line, ClinicalState.Unavailable,
                $"Indication check unavailable — {u.Reason}.",
                $"تعذّر التحقق من دواعي الاستعمال — {u.Reason}.",
                provenance: null);
        }

        var available = (Fetched<IReadOnlyDictionary<Guid, DrugIndicationFact>>.Available)indications;
        var provenance = available.Provenance;

        // A diagnosis is what the check compares against. With none recorded there is nothing to compare, and
        // saying so is the honest answer — doc 43 §6 is explicit that this must not report OK.
        if (diagnoses.Count == 0)
        {
            return Clinical(line, ClinicalState.NotChecked,
                "Not checked — no diagnosis recorded on this encounter.",
                "لم يتم التحقق — لا يوجد تشخيص مسجل في هذه الزيارة.",
                provenance);
        }

        if (!available.Value.TryGetValue(line.DrugId, out var fact) || fact.IcdCategories.Count == 0)
        {
            return Clinical(line, ClinicalState.NotChecked,
                "Not checked — no indication data is recorded for this medicine.",
                "لم يتم التحقق — لا توجد بيانات دواعي استعمال مسجلة لهذا الدواء.",
                provenance);
        }

        /*
         * 28.7 — A HIERARCHY WALK, NOT A TRUNCATION (doc 44 §6).
         *
         * This used to cut both sides to three characters and intersect. That works for the common case —
         * a drug indicated at category level, a diagnosis coded at subcategory level — and fails in ways it
         * cannot see: a BLOCK-level indication ("J00-J06", acute upper respiratory infections) is
         * inexpressible by truncation, and an indication more specific than three characters is silently
         * widened to its whole category.
         *
         * The rule now is: an indication at node L matches a diagnosis D when D is a DESCENDANT-OR-SELF of L.
         */
        var ancestorsOf = diagnosisContextValue.Ancestors ?? new Dictionary<string, IReadOnlyList<string>>();

        var matched = new List<string>();
        var possible = new List<string>();

        foreach (var node in fact.IcdCategories)
        {
            foreach (var diagnosis in diagnoses)
            {
                var chain = ancestorsOf.TryGetValue(IcdCodes.Normalize(diagnosis), out var a) ? a : [];

                if (IcdCodes.IsDescendantOrSelf(diagnosis, chain, node))
                {
                    matched.Add(node);
                    break;
                }

                // The INVERSE: the diagnosis is less specific than the indication. Neither a clean hit nor a
                // miss — see the note on DrugIndicationFact.NodeAncestors.
                var nodeChain = fact.NodeAncestors?.TryGetValue(IcdCodes.Normalize(node), out var na) == true ? na : [];
                if (IcdCodes.IsLessSpecificThan(diagnosis, node, nodeChain)) possible.Add(node);
            }
        }

        if (matched.Count > 0)
        {
            var codes = matched.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            return Clinical(line, ClinicalState.Ok,
                $"Listed indication ({string.Join(", ", codes)}).",
                $"من دواعي الاستعمال المسجلة ({string.Join("، ", codes)}).",
                provenance);
        }

        if (possible.Count > 0)
        {
            var codes = possible.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            return Clinical(line, ClinicalState.NotChecked,
                $"Possible indication ({string.Join(", ", codes)}), but the recorded diagnosis is less "
                + "specific than the indication — code the diagnosis more precisely to confirm.",
                $"قد يكون من دواعي الاستعمال ({string.Join("، ", codes)})، لكن التشخيص المسجل أقل تحديدًا من "
                + "دواعي الاستعمال — يُرجى ترميز التشخيص بدقة أكبر للتأكيد.",
                provenance);
        }

        // Off-label prescribing is legitimate and common. This is a warning forever — never a block.
        return Clinical(line, ClinicalState.Warning,
            "Not a listed indication for the recorded diagnosis. Off-label use may be appropriate; "
            + "give a reason to proceed.",
            "ليس من دواعي الاستعمال المسجلة للتشخيص المدوَّن. قد يكون الاستخدام خارج التصنيف مناسبًا؛ "
            + "يرجى ذكر السبب للمتابعة.",
            provenance);
    }

    private static Finding Clinical(
        PrescriptionLineInput line, ClinicalState state, string en, string ar, ProvenanceInfo? provenance)
        => Finding.Clinical(line.LineId, line.DrugId, CheckKind.Indication, state, en, ar, provenance);
}

internal static class InteractionChecks
{
    /// <summary>
    /// Checks every pair among the prescribed lines, and each line against the beneficiary's active
    /// medications.
    /// </summary>
    /// <remarks>
    /// Cross-line by nature: n lines produce n(n-1)/2 pairs, each considered once regardless of the order the
    /// two drugs appear in the source table. The interaction data is LOCAL — <c>masterdata.drug_interaction</c>
    /// — because the free external interaction APIs this would otherwise use were withdrawn (NLM's in
    /// January 2024, DrugBank's free tier in March 2026). A safety check that depends on an unlicensed free
    /// endpoint is a safety check that disappears without notice (doc 43 §2).
    /// </remarks>
    internal static IReadOnlyList<Finding> Evaluate(
        ValidationRequest request,
        Fetched<InteractionTable> interactions,
        Fetched<ActiveMedications> activeMedications)
    {
        if (interactions is Fetched<InteractionTable>.Unavailable u)
        {
            return [.. request.Lines.Select(l => Finding.Clinical(
                l.LineId, l.DrugId, CheckKind.Interaction, ClinicalState.Unavailable,
                $"Interaction check unavailable — {u.Reason}.",
                $"تعذّر التحقق من التداخلات الدوائية — {u.Reason}.",
                provenance: null))];
        }

        var available = (Fetched<InteractionTable>.Available)interactions;
        var table = available.Value;
        var provenance = available.Provenance;

        // An empty list finds nothing, which is not the same as there being nothing to find.
        if (table.KnownPairCount == 0)
        {
            return [.. request.Lines.Select(l => Finding.Clinical(
                l.LineId, l.DrugId, CheckKind.Interaction, ClinicalState.NotChecked,
                "Not checked — the Mersal interaction list contains 0 pairs.",
                "لم يتم التحقق — قائمة التداخلات الدوائية لدى مرسال لا تحتوي على أي أزواج.",
                provenance))];
        }

        var lookup = new Dictionary<(Guid, Guid), InteractionFact>();
        foreach (var pair in table.Pairs) lookup[Key(pair.DrugAId, pair.DrugBId)] = pair;

        var findings = new List<Finding>();
        var flagged = new HashSet<Guid>();

        // Every unordered pair of lines, considered once.
        for (var i = 0; i < request.Lines.Count; i++)
        {
            for (var j = i + 1; j < request.Lines.Count; j++)
            {
                var a = request.Lines[i];
                var b = request.Lines[j];
                if (a.DrugId == b.DrugId) continue;
                if (!lookup.TryGetValue(Key(a.DrugId, b.DrugId), out var hit)) continue;

                findings.Add(Interaction(a, hit, $"with {b.DrugName}", $"مع {b.DrugName}", provenance, b.LineId));
                findings.Add(Interaction(b, hit, $"with {a.DrugName}", $"مع {a.DrugName}", provenance, a.LineId));
                flagged.Add(a.LineId);
                flagged.Add(b.LineId);
            }
        }

        // Each line against what the beneficiary is ALREADY taking — behind Fetched<T>, so "we could not
        // ask" is a different answer from "we asked and there is nothing", and neither is silence.
        //
        // 32.1: this loop existed from phase 28 and ran zero times, because its input was a plain list on
        // the request that both call sites passed empty. Every line then fell through to the Ok below and
        // was reported "no interaction found" — the phase-26 lesson (Unavailable is not Ok) defeated by an
        // input rather than by a catch.
        if (activeMedications is Fetched<ActiveMedications>.Unavailable unavailableMeds)
        {
            foreach (var line in request.Lines)
            {
                findings.Add(Finding.Clinical(
                    line.LineId, line.DrugId, CheckKind.Interaction, ClinicalState.Unavailable,
                    $"Interaction check incomplete — the patient's current medications could not be read "
                    + $"({unavailableMeds.Reason}). The lines being written now were checked against each other.",
                    $"التحقق من التداخلات غير مكتمل — تعذّرت قراءة أدوية المريض الحالية "
                    + $"({unavailableMeds.Reason}). تم التحقق من الأسطر المكتوبة الآن مقابل بعضها.",
                    provenance));
            }

            return findings;
        }

        var currentMedications = ((Fetched<ActiveMedications>.Available)activeMedications).Value.Items;

        foreach (var line in request.Lines)
        {
            foreach (var active in currentMedications.DistinctBy(m => m.DrugId))
            {
                // Re-prescribing a medicine the patient is continuing is the ordinary case. Pairing a drug
                // with itself would report an interaction on every repeat script.
                if (active.DrugId == line.DrugId) continue;
                if (!lookup.TryGetValue(Key(line.DrugId, active.DrugId), out var hit)) continue;

                findings.Add(Interaction(line, hit,
                    $"with {active.DrugName}, which the patient is already taking ({active.Source})",
                    $"مع {active.DrugName}، وهو دواء يتناوله المريض بالفعل ({active.Source})",
                    provenance, relatedLineId: null));
                flagged.Add(line.LineId);
            }
        }

        // A line with no interaction found genuinely was checked — and the sentence says against WHAT.
        var medsCoverageEn = currentMedications.Count == 0
            ? "no current medications recorded for this patient"
            : $"{currentMedications.Count} current medication(s)";
        var medsCoverageAr = currentMedications.Count == 0
            ? "لا توجد أدوية حالية مسجّلة لهذا المريض"
            : $"{currentMedications.Count} من الأدوية الحالية";

        foreach (var line in request.Lines.Where(l => !flagged.Contains(l.LineId)))
        {
            findings.Add(Finding.Clinical(
                line.LineId, line.DrugId, CheckKind.Interaction, ClinicalState.Ok,
                // Coverage stated, not implied (doc 44 §8). "No interaction found" against a curated list
                // of 13 pairs is a much weaker statement than the same words against a licensed database,
                // and a prescriber is entitled to know which one they are reading. 32.1 adds the second
                // half of the coverage: an empty medication list and a checked one read identically
                // otherwise, and they are not the same claim.
                $"No interaction found (checked against Mersal's interaction list: {table.KnownPairCount} "
                + $"ingredient pairs{Updated(table.RulesUpdatedAt)}, and {medsCoverageEn}).",
                $"لم يتم العثور على تداخلات (تم التحقق مقابل قائمة التداخلات لدى مرسال: "
                + $"{table.KnownPairCount} زوجًا من المواد الفعالة{UpdatedAr(table.RulesUpdatedAt)}، و{medsCoverageAr}).",
                provenance));
        }

        return findings;
    }

    /// <summary>
    /// One interaction, worded so a prescriber can act on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to read "<c>Major interaction with Klacid</c>" — a severity, a brand name, and nothing to
    /// do about it. The severity is no longer interpolated into the sentence at all: it is a first-class
    /// field the UI renders as its own chip (28.4), and repeating it in prose would be the same cue twice
    /// while the useful sentence went unwritten.
    /// </para>
    /// <para>
    /// What replaces it is design 44 §3's three fields. MECHANISM says why, CLINICAL EFFECT says what
    /// happens, MANAGEMENT says what to do instead — and management is what converts an alert from an
    /// obstacle into advice, so it is in the message rather than behind a disclosure.
    /// </para>
    /// </remarks>
    private static Finding Interaction(
        PrescriptionLineInput line, InteractionFact fact,
        string withEn, string withAr, ProvenanceInfo provenance, Guid? relatedLineId)
    {
        // The molecules, where the rule matched on them — "warfarin with ATC class M01A". A prescriber told
        // only the brand names cannot carry the warning to the next brand they reach for.
        var basisEn = fact.MatchedOn is { Length: > 0 } m ? $" ({m})" : "";

        var detailEn = Join(" ", fact.MechanismEn, fact.ClinicalEffectEn, fact.ManagementEn, fact.Description);
        var detailAr = Join(" ", fact.MechanismAr, fact.ClinicalEffectAr, fact.ManagementAr, fact.Description);

        return Finding.Clinical(
            line.LineId, line.DrugId, CheckKind.Interaction, ClinicalState.Warning,
            $"Interaction {withEn}{basisEn}." + (detailEn.Length == 0 ? "" : $" {detailEn}"),
            $"تداخل دوائي {withAr}{basisEn}." + (detailAr.Length == 0 ? "" : $" {detailAr}"),
            provenance, fact.Severity, relatedLineId,
            // The citation travels as quoted reference rather than as authored text: it is the source's
            // words, and an advisory a clinician cannot attribute is one they are right to ignore.
            referenceText: fact.Citation);
    }

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Updated(DateTimeOffset? at) =>
        at is { } d ? $", updated {d:d MMM yyyy}" : "";

    private static string UpdatedAr(DateTimeOffset? at) =>
        at is { } d ? $"، محدَّثة في {d:yyyy-MM-dd}" : "";

    /// <summary>Order-insensitive key — the source records a pair once, in whichever order.</summary>
    private static (Guid, Guid) Key(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);

}

/// <summary>
/// The prescribed medicine against the beneficiary's recorded allergies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this checker is written defensively.</b> Until phase 28 it reported
/// <see cref="ClinicalState.Ok"/> — "no conflict with the 3 recorded allergy/allergies" — on every
/// prescription ever written on this platform, because the matcher behind it compared allergen codes
/// (<c>ALG-PENICILLIN</c>) against a drug's ATC ancestor chain (<c>J01C</c>). Two disjoint sets; an
/// expression that could not be true; a green tick on a check that never ran.
/// </para>
/// <para>
/// A missing check is a gap. A missing check that <i>reassures</i> is a hazard, because the prescriber
/// stops looking. So the rule here is deliberately stricter than "don't report a match you didn't find":
/// <b>Ok requires that every recorded allergy was actually compared</b>. Anything less is
/// <see cref="ClinicalState.NotChecked"/>, naming what was left out.
/// </para>
/// </remarks>
internal static class AllergyChecks
{
    internal static Finding Evaluate(PrescriptionLineInput line, Fetched<AllergyScreen> allergies)
    {
        if (allergies is Fetched<AllergyScreen>.Unavailable u)
        {
            return Clinical(line, ClinicalState.Unavailable,
                $"Allergy check unavailable — {u.Reason}.",
                $"تعذّر التحقق من الحساسية — {u.Reason}.",
                provenance: null);
        }

        var available = (Fetched<AllergyScreen>.Available)allergies;
        var screen = available.Value;
        var provenance = available.Provenance;

        // No recorded allergy history is not a negative allergy history.
        if (screen.RecordedAllergenCount == 0)
        {
            return Clinical(line, ClinicalState.NotChecked,
                "Not checked — no allergies are recorded for this patient.",
                "لم يتم التحقق — لا توجد حساسية مسجلة لهذا المريض.",
                provenance);
        }

        // What was recorded but not compared, in the prescriber's words rather than ours.
        // The medicine itself cannot be compared with anything: no recorded active ingredient and no ATC
        // class. Reported before the allergen gaps because it is a different fault with a different owner —
        // this product's catalogue row, not the patient's allergy record — and a prescriber told
        // "'Penicillins' has no mapping" would go looking in the wrong place.
        if (screen.UnresolvableDrugIds?.Contains(line.DrugId) == true)
        {
            return Clinical(line, ClinicalState.NotChecked,
                $"Not checked — no active ingredient or drug class is recorded for {line.DrugName}, so the "
                + $"{screen.RecordedAllergenCount} recorded allergy/allergies could not be compared against it.",
                $"لم يتم التحقق — لا توجد مادة فعالة أو فئة دوائية مسجلة لـ {line.DrugName}، لذلك تعذّرت "
                + $"مقارنتها بالحساسية المسجلة ({screen.RecordedAllergenCount}).",
                provenance);
        }

        (string En, string Ar)? unscreened = screen.UnmappedAllergens.Count == 0
            ? null
            : (En: $"{Join(screen.UnmappedAllergens, ", ")} "
                + (screen.UnmappedAllergens.Count == 1 ? "has" : "have")
                + " no mapping to an ingredient or drug class in Mersal's catalogue, so "
                + (screen.UnmappedAllergens.Count == 1 ? "it was" : "they were") + " not checked",
               Ar: $"{Join(screen.UnmappedAllergens, "، ")} — لا يوجد ربط بمادة فعالة أو فئة دوائية في "
                + "كتالوج مرسال، لذلك لم يتم التحقق منها");

        // A conflict is an ANSWER, and it outranks the gap. The gap is still disclosed in the same
        // sentence: a prescriber told "conflicts with penicillin" would otherwise reasonably assume the
        // rest of the history had been screened and found clean.
        var conflict = screen.Conflicts.FirstOrDefault(c => c.DrugId == line.DrugId);
        if (conflict is not null)
        {
            var tailEn = unscreened is null ? "" : $" Note: {unscreened.Value.En}.";
            var tailAr = unscreened is null ? "" : $" ملاحظة: {unscreened.Value.Ar}.";

            // The management line, prominently and not in a footnote (design 44 §3, §6). "Consider a
            // cephalosporin with a different side chain" is what changes the prescription; "allergy
            // conflict" only stops it.
            var manageEn = string.IsNullOrWhiteSpace(conflict.ManagementEn) ? "" : $" {conflict.ManagementEn}";
            var manageAr = string.IsNullOrWhiteSpace(conflict.ManagementAr) ? "" : $" {conflict.ManagementAr}";

            return Finding.Clinical(
                line.LineId, line.DrugId, CheckKind.Allergy, ClinicalState.Warning,
                $"This medicine {conflict.MatchedOn}.{manageEn}{tailEn}",
                $"هذا الدواء {conflict.MatchedOn}.{manageAr}{tailAr}",
                provenance,
                // A direct match to the recorded allergy is Major; a low-confidence cross-reaction is
                // Moderate and renders inline. Weighting them alike is what teaches prescribers to dismiss
                // both, and the harm of over-warning here is concrete: blanket cephalosporin avoidance after
                // a penicillin label means treating with a worse antibiotic (design 44 §8).
                severity: conflict.Severity,
                referenceText: conflict.Citation);
        }

        // An unmapped allergen is a gap in OUR data, and the one this phase exists for. Naming it is what
        // makes it fixable — by a pharmacist adding the mapping, not by a prescriber guessing.
        if (unscreened is not null)
        {
            var checkedSoFar = screen.ScreenedAllergenCount == 0
                ? "No recorded allergy could be checked against this medicine"
                : $"Checked against {screen.ScreenedAllergenCount} of {screen.RecordedAllergenCount} "
                    + "recorded allergies and found no conflict";
            var checkedSoFarAr = screen.ScreenedAllergenCount == 0
                ? "تعذّر التحقق من أي حساسية مسجلة مقابل هذا الدواء"
                : $"تم التحقق مقابل {screen.ScreenedAllergenCount} من {screen.RecordedAllergenCount} "
                    + "حساسية مسجلة ولم يُعثر على تعارض";

            return Clinical(line, ClinicalState.NotChecked,
                $"{checkedSoFar} — {unscreened.Value.En}.",
                $"{checkedSoFarAr} — {unscreened.Value.Ar}.",
                provenance);
        }

        // Nothing was unmappable, and nothing was screened: every recorded allergy is a food or an
        // environmental one, which is not a question about a medicine. That is NOT an all-clear on this
        // patient's drug-allergy history — that history is simply unrecorded, which is the case the check
        // already refuses to pass a few lines above.
        if (screen.ScreenedAllergenCount == 0)
        {
            return Clinical(line, ClinicalState.NotChecked,
                $"Not checked — no medicine-related allergy is recorded for this patient "
                + $"(the {screen.RecordedAllergenCount} on file are food or environmental).",
                $"لم يتم التحقق — لا توجد حساسية دوائية مسجلة لهذا المريض "
                + $"(المسجَّل {screen.RecordedAllergenCount} من نوع غذائي أو بيئي).",
                provenance);
        }

        // Everything recorded was compared, and nothing matched. The only path to Ok.
        return Clinical(line, ClinicalState.Ok,
            $"No conflict with the {screen.ScreenedAllergenCount} recorded allergy/allergies, "
            + "all of which were checked.",
            $"لا يوجد تعارض مع الحساسية المسجلة ({screen.ScreenedAllergenCount})، وقد تم التحقق منها جميعًا.",
            provenance);
    }

    private static string Join(IReadOnlyList<string> names, string separator) =>
        string.Join(separator, names.Select(n => $"'{n}'"));

    private static Finding Clinical(
        PrescriptionLineInput line, ClinicalState state, string en, string ar, ProvenanceInfo? provenance)
        => Finding.Clinical(line.LineId, line.DrugId, CheckKind.Allergy, state, en, ar, provenance);
}

/// <summary>
/// Drug–drug interactions read from manufacturer label text (openFDA), across the lines of one prescription.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists alongside the curated check.</b> <c>masterdata.drug_interaction</c> holds zero pairs,
/// so <see cref="InteractionChecks"/> reports "not checked" on every line of every prescription. Label text
/// is currently the only source on the platform capable of raising an interaction at all.
/// </para>
/// <para>
/// <b>Why it may warn but never reassure.</b> A label's interactions section is a narrative written per
/// product, not a curated matrix — openFDA publishes no structured interaction data. A mention is therefore
/// real evidence, but a silence is not: the absence of a name in prose does not mean the absence of an
/// interaction. So a clean scan returns <see cref="ClinicalState.NotChecked"/>, never
/// <see cref="ClinicalState.Ok"/>. Rendering it green would manufacture exactly the false assurance the five
/// -state model exists to prevent.
/// </para>
/// <para>
/// <b>Both directions.</b> Two labels are consulted for each pair — A's section is searched for B, and B's
/// for A. Manufacturers do not document interactions symmetrically, and a single-direction scan misses the
/// half where only the older drug's label carries the warning.
/// </para>
/// </remarks>
internal static class LabelInteractionChecks
{
    internal static IReadOnlyList<Finding> Evaluate(ValidationRequest request, Fetched<LabelEvidence> labels)
    {
        // Which lines this check has a question about at all: those with another, DIFFERENT drug alongside
        // them. Established before the source is even consulted, because a check with nothing to answer is
        // inapplicable whether or not the source is up — an outage report on a single-drug prescription is
        // noise about work that was never going to happen.
        //
        // Saying nothing, rather than "not checked", also matters beyond tidiness: this pass can never return
        // Ok, so any finding it emits stops its line from ever summarising as Ok. Parking every single-drug
        // prescription in the unchecked state for ever would drain the meaning out of that state on the
        // prescriptions where something really was skipped.
        var pairable = request.Lines
            .Select(line => (Line: line,
                Others: request.Lines.Where(o => o.LineId != line.LineId && o.DrugId != line.DrugId).ToList()))
            .Where(x => x.Others.Count > 0)
            .ToList();

        if (pairable.Count == 0) return [];

        if (labels is Fetched<LabelEvidence>.Unavailable u)
        {
            return [.. pairable.Select(x => Finding.Clinical(
                x.Line.LineId, x.Line.DrugId, CheckKind.Interaction, ClinicalState.Unavailable,
                $"Manufacturer-label interaction check unavailable — {u.Reason}.",
                $"تعذّر التحقق من التداخلات من نشرة الشركة المصنِّعة — {u.Reason}.",
                provenance: null))];
        }

        var available = (Fetched<LabelEvidence>.Available)labels;
        var evidence = available.Value;
        var provenance = available.Provenance;
        var findings = new List<Finding>();

        foreach (var (line, others) in pairable)
        {
            // A source failure is NOT "nothing found". It outranks Warning in the roll-up precisely so a
            // prescriber cannot read an outage as a quiet pass.
            if (evidence.Failed.TryGetValue(line.DrugId, out var failure))
            {
                findings.Add(Finding.Clinical(
                    line.LineId, line.DrugId, CheckKind.Interaction, ClinicalState.Unavailable,
                    $"Manufacturer-label interaction check unavailable — {failure}.",
                    $"تعذّر التحقق من التداخلات من نشرة الشركة المصنِّعة — {failure}.",
                    provenance: null));
                continue;
            }

            if (!evidence.ByDrug.TryGetValue(line.DrugId, out var own))
            {
                var reason = evidence.Unmatched.TryGetValue(line.DrugId, out var why) ? why : "no label was retrieved";
                findings.Add(Note(line, provenance,
                    $"Not checked — {reason}.",
                    $"لم يتم التحقق — {reason}."));
                continue;
            }

            var mentioned = new List<Finding>();
            var unreadable = new List<string>();

            foreach (var other in others)
            {
                if (!evidence.ByDrug.TryGetValue(other.DrugId, out var peer))
                {
                    unreadable.Add(other.DrugName);

                    // One label is still one label: A's own section may name a drug whose label we could not
                    // fetch, and that warning is worth as much as any other.
                    var oneSided = LabelInteractionScan.Find(own.InteractionsText, [other.DrugName]);
                    if (oneSided is not null) mentioned.Add(Warn(line, other, own, oneSided, provenance));
                    continue;
                }

                var hit = LabelInteractionScan.Find(own.InteractionsText, peer.Aliases);
                if (hit is not null)
                {
                    mentioned.Add(Warn(line, other, own, hit, provenance));
                    continue;
                }

                var reverse = LabelInteractionScan.Find(peer.InteractionsText, own.Aliases);
                if (reverse is not null) mentioned.Add(Warn(line, other, peer, reverse, provenance));
            }

            if (mentioned.Count > 0)
            {
                findings.AddRange(mentioned);
                continue;
            }

            if (unreadable.Count > 0)
            {
                findings.Add(Note(line, provenance,
                    $"Not checked against {string.Join(", ", unreadable)} — no manufacturer label was retrieved for "
                    + (unreadable.Count == 1 ? "it" : "them") + ".",
                    $"لم يتم التحقق مقابل {string.Join("، ", unreadable)} — لم يتم الحصول على نشرة الشركة المصنِّعة."));
                continue;
            }

            // Every label present, no drug named in any of them. NOT an all-clear — see the class remarks.
            findings.Add(Note(line, provenance,
                "No interaction named in the manufacturer labels for the medicines on this prescription. "
                + "This is not an all-clear: a label's interactions section is written as prose, not as a "
                + "complete list, so an interaction can exist without being named.",
                "لم يُذكر أي تداخل في نشرات الشركات المصنِّعة للأدوية في هذه الوصفة. وهذا لا يعني الخلو من "
                + "التداخلات: فقسم التداخلات في النشرة مكتوب كنص وصفي وليس قائمة كاملة، وقد يوجد تداخل دون ذكره."));
        }

        return findings;
    }

    private static Finding Warn(
        PrescriptionLineInput line, PrescriptionLineInput other, DrugLabelFact source,
        LabelMention mention, ProvenanceInfo provenance)
        => Finding.Clinical(
            line.LineId, line.DrugId, CheckKind.Interaction, ClinicalState.Warning,
            $"The {source.MatchedGenericName} label names {mention.Term} in its interactions section, and "
            + $"{other.DrugName} is on this prescription. Read the manufacturer's wording below and give a "
            + "reason to proceed.",
            $"تذكر نشرة {source.MatchedGenericName} المادة {mention.Term} ضمن قسم التداخلات الدوائية، "
            + $"و{other.DrugName} موجود في هذه الوصفة. يرجى قراءة نص الشركة المصنِّعة أدناه (بالإنجليزية) "
            + "وذكر السبب للمتابعة.",
            provenance,
            // No severity. The label states an effect, not a grade, and assigning one here would be this
            // code inventing a clinical judgement it has no source for.
            severity: null,
            relatedLineId: other.LineId,
            referenceText: mention.Sentence);

    private static Finding Note(
        PrescriptionLineInput line, ProvenanceInfo provenance, string en, string ar)
        => Finding.Clinical(
            line.LineId, line.DrugId, CheckKind.Interaction, ClinicalState.NotChecked, en, ar, provenance);
}

internal static class DoseChecks
{
    internal static Finding Evaluate(
        PrescriptionLineInput line,
        Fetched<IReadOnlyDictionary<Guid, DosingRuleFact>> rules,
        Fetched<LabelEvidence> labels,
        Fetched<PatientContext> patient,
        DateTimeOffset now)
    {
        if (rules is Fetched<IReadOnlyDictionary<Guid, DosingRuleFact>>.Unavailable u)
        {
            return Clinical(line, ClinicalState.Unavailable,
                $"Dose check unavailable — {u.Reason}.",
                $"تعذّر التحقق من الجرعة — {u.Reason}.",
                provenance: null);
        }

        var available = (Fetched<IReadOnlyDictionary<Guid, DosingRuleFact>>.Available)rules;
        var provenance = available.Provenance;

        // The honest default, and the common one. Doc 43 §2: a dose cannot be derived from label prose, so
        // outside the curated subset the answer is "no rule", never an implied endorsement.
        if (!available.Value.TryGetValue(line.DrugId, out var rule))
        {
            return LabelReference(line, provenance, labels);
        }

        /*
         * 28.8 — A MISSING INPUT IS "NOT CHECKED, NAMING IT", NEVER "OK" (doc 44 §4, invariant 11).
         *
         * This is the whole safety argument for shipping a partial dose check. A rule that needs a weight,
         * evaluated without one, must say "weight required for paediatric dosing" — not pass the line. The
         * same for renal function: the platform stores lab results as free text, so there is no eGFR to read,
         * and a dose check that silently ignores renal clearance on a renally-cleared drug in a patient with
         * kidney disease is worse than no check at all, because it reassures.
         */
        var context = patient is Fetched<PatientContext>.Available p ? p.Value : null;

        if (rule.IsWeightBased)
        {
            if (context is null)
            {
                return Clinical(line, ClinicalState.NotChecked,
                    "Dose not checked — this medicine is dosed by weight and no patient record was available.",
                    "لم يتم التحقق من الجرعة — هذا الدواء يُجرَّع حسب الوزن ولم يتوفر سجل المريض.",
                    provenance);
            }

            if (context.WeightKg is null or <= 0)
            {
                return Clinical(line, ClinicalState.NotChecked,
                    "Dose not checked — weight is required for weight-based dosing and none is recorded. "
                    + "Record a weight to check this dose.",
                    "لم يتم التحقق من الجرعة — الوزن مطلوب للتجريع حسب الوزن ولا يوجد وزن مسجل. "
                    + "يُرجى تسجيل الوزن للتحقق من هذه الجرعة.",
                    provenance);
            }

            if (!context.HasCurrentWeight(now))
            {
                // A stale weight is not a current weight. Naming the date is what lets the prescriber
                // decide whether to re-weigh or to proceed on their own judgement.
                var age = context.WeightMeasuredAt is { } at ? $" (last recorded {at:d MMM yyyy})" : "";
                return Clinical(line, ClinicalState.NotChecked,
                    $"Dose not checked — the recorded weight is too old to dose against{age}. "
                    + $"A weight within {(context.IsChild ? 30 : 90)} days is needed for this medicine.",
                    $"لم يتم التحقق من الجرعة — الوزن المسجل قديم ولا يصلح للتجريع{age}. "
                    + $"يلزم وزن خلال {(context.IsChild ? 30 : 90)} يومًا لهذا الدواء.",
                    provenance);
            }
        }

        // A renally-cleared drug states the gap rather than passing over it.
        if (rule.RequiresRenalFunction && (context is null || context.Renal == RenalFunction.Unknown))
        {
            return Clinical(line, ClinicalState.NotChecked,
                "Dose not checked — this medicine needs renal dose adjustment, and eGFR is not available on "
                + "this platform (laboratory results are not stored as structured values).",
                "لم يتم التحقق من الجرعة — يحتاج هذا الدواء إلى تعديل حسب وظائف الكلى، ومعدل الترشيح "
                + "الكبيبي غير متاح في هذه المنصة (نتائج المختبر غير مخزَّنة كقيم منظَّمة).",
                provenance);
        }

        var breaches = new List<(string En, string Ar)>();

        if (rule.MaxDailyDose is { } maxDaily && line.DoseAmount is { } dose && line.TimesPerDay is { } perDay)
        {
            var dailyTotal = dose * perDay;
            if (dailyTotal > maxDaily)
            {
                breaches.Add((
                    $"daily dose {dailyTotal}{rule.DoseUnit} exceeds the maximum {maxDaily}{rule.DoseUnit}",
                    $"الجرعة اليومية {dailyTotal}{rule.DoseUnit} تتجاوز الحد الأقصى {maxDaily}{rule.DoseUnit}"));
            }
        }

        if (rule.MaxDurationDays is { } maxDays && line.DurationDays is { } days && days > maxDays)
        {
            breaches.Add((
                $"duration {days} days exceeds the maximum {maxDays} days",
                $"مدة العلاج {days} يومًا تتجاوز الحد الأقصى {maxDays} يومًا"));
        }

        if (breaches.Count > 0)
        {
            return Clinical(line, ClinicalState.Warning,
                "Dosing rule exceeded: " + string.Join("; ", breaches.Select(b => b.En)) + ". Give a reason to proceed.",
                "تم تجاوز قاعدة الجرعات: " + string.Join("؛ ", breaches.Select(b => b.Ar)) + ". يرجى ذكر السبب للمتابعة.",
                provenance);
        }

        // The recommended range beside what was entered — the feature design 44 §4 actually asked for, and
        // more useful than a pass/fail: it informs the override rather than merely obstructing it.
        var range = rule.RecommendedRange();

        return Clinical(line, ClinicalState.Ok,
            $"Within the configured dosing rule.{range}",
            $"ضمن قاعدة الجرعات المُهيأة.{range}",
            provenance);
    }

    /// <summary>
    /// No curated rule exists, so the manufacturer's own dosing section is shown for the prescriber to read
    /// against what they typed — as <b>reference</b>, never as a verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state stays <see cref="ClinicalState.NotChecked"/>, and that is the point. openFDA publishes
    /// dosing as prose ("individualize dosing regimen for each patient, and adjust based on INR response"),
    /// with no structured maximum daily dose or treatment length anywhere in the dataset. Extracting a
    /// ceiling from that text would produce a number this platform cannot defend, and it would fail hardest
    /// on the narrative-dosed drugs — warfarin, insulin, cytotoxics — where a wrong ceiling does the most
    /// harm. Showing the text and declining to grade it is the useful, honest half of what was asked for.
    /// </para>
    /// <para>
    /// The label's marketed strengths are included in the same reference block rather than compared
    /// mechanically. FDA strengths are the strengths of <i>US</i> products; Egyptian presentations differ
    /// legitimately and often, so a mismatch warning would fire constantly on correct prescriptions — and
    /// because a warning requires an acknowledgement reason before submission, it would tax every one of
    /// them. A prescriber reading the list can see a discrepancy that matters far better than a rule that
    /// cannot tell a market difference from an error.
    /// </para>
    /// </remarks>
    private static Finding LabelReference(
        PrescriptionLineInput line, ProvenanceInfo provenance, Fetched<LabelEvidence> labels)
    {
        const string NoRuleEn = "Dose not checked — no dosing rule is configured for this medicine.";
        const string NoRuleAr = "لم يتم التحقق من الجرعة — لا توجد قاعدة جرعات مُهيأة لهذا الدواء.";

        if (labels is not Fetched<LabelEvidence>.Available available
            || !available.Value.ByDrug.TryGetValue(line.DrugId, out var label)
            || string.IsNullOrWhiteSpace(label.DosingText))
        {
            return Clinical(line, ClinicalState.NotChecked, NoRuleEn, NoRuleAr, provenance);
        }

        var reference = string.IsNullOrWhiteSpace(label.StrengthsText)
            ? label.DosingText
            : $"{label.DosingText}\n\nDOSAGE FORMS AND STRENGTHS\n{label.StrengthsText}";

        return Finding.Clinical(
            line.LineId, line.DrugId, CheckKind.DoseDuration, ClinicalState.NotChecked,
            NoRuleEn + $" The manufacturer's labelled dosing for {label.MatchedGenericName} is shown below for "
            + "reference — it has NOT been compared with what you prescribed.",
            NoRuleAr + $" فيما يلي جرعات النشرة المعتمدة لـ {label.MatchedGenericName} للاطلاع فقط "
            + "(بالإنجليزية) — ولم تتم مقارنتها بما وصفته.",
            provenance,
            referenceText: reference);
    }

    private static Finding Clinical(
        PrescriptionLineInput line, ClinicalState state, string en, string ar, ProvenanceInfo? provenance)
        => Finding.Clinical(line.LineId, line.DrugId, CheckKind.DoseDuration, state, en, ar, provenance);
}

internal static class BenefitChecks
{
    /// <summary>
    /// The benefit pre-check. In phase 26 the seam always reports NotChecked; phase 27 supplies real
    /// outcomes through the same <see cref="BenefitOutcome"/> shape, so it swaps an implementation rather
    /// than restructuring the engine.
    /// </summary>
    internal static IReadOnlyList<Finding> Evaluate(
        ValidationRequest request, Fetched<IReadOnlyList<BenefitOutcome>> benefit)
    {
        if (benefit is Fetched<IReadOnlyList<BenefitOutcome>>.Unavailable u)
        {
            return [.. request.Lines.Select(l => Finding.Benefit(
                l.LineId, l.DrugId, BenefitState.Unavailable,
                $"Benefit check unavailable — {u.Reason}.",
                $"تعذّر التحقق من التغطية — {u.Reason}.",
                provenance: null))];
        }

        var available = (Fetched<IReadOnlyList<BenefitOutcome>>.Available)benefit;
        var byLine = available.Value.ToDictionary(o => o.LineId);

        return
        [
            .. request.Lines.Select(l => byLine.TryGetValue(l.LineId, out var outcome)
                ? Finding.Benefit(l.LineId, l.DrugId, outcome.State, outcome.MessageEn, outcome.MessageAr,
                    outcome.Provenance ?? available.Provenance)
                : Finding.Benefit(l.LineId, l.DrugId, BenefitState.NotChecked,
                    "Benefit rules are not yet evaluated at prescribing time.",
                    "لم يتم بعد تقييم قواعد التغطية عند وصف الدواء.",
                    available.Provenance)),
        ];
    }
}

/// <summary>
/// Two lines that duplicate each other — the same molecule, or the same therapeutic class (28.5, doc 44 §7).
/// </summary>
/// <remarks>
/// <para>
/// <b>What phase 26 did instead.</b> The interaction pass opened with
/// <c>if (a.DrugId == b.DrugId) continue;</c>, which silently dropped the same product prescribed twice, and
/// nothing anywhere compared two DIFFERENT products for a shared ingredient. So the commonest real
/// prescribing duplication — two trade names holding one molecule — was invisible by construction.
/// </para>
/// <para>
/// <b>Why paracetamol is the case to get right.</b> A prescriber writes paracetamol for the fever and a
/// cold-and-flu compound for the symptoms. Both contain it, the combined daily total crosses the hepatotoxic
/// ceiling, and nothing on either box says so. It is the classic accidental overdose, and it is only
/// detectable because products now decompose into molecules.
/// </para>
/// <para>
/// <b>Why it warns rather than blocks.</b> Deliberate duplication is legitimate — a loading dose alongside
/// maintenance, or two agents of one class during a cross-taper. This is a clinical check, so it may only
/// ever warn (doc 43 invariant 1).
/// </para>
/// </remarks>
internal static class DuplicationChecks
{
    internal static IReadOnlyList<Finding> Evaluate(
        ValidationRequest request, Fetched<IReadOnlyDictionary<Guid, DrugComposition>> compositions)
    {
        // Nothing to compare against on a single-line prescription. Reporting "not checked" on every one of
        // them would drain the meaning out of that state where something really was skipped.
        if (request.Lines.Count < 2) return [];

        if (compositions is Fetched<IReadOnlyDictionary<Guid, DrugComposition>>.Unavailable u)
        {
            return [.. request.Lines.Select(l => Finding.Clinical(
                l.LineId, l.DrugId, CheckKind.Duplication, ClinicalState.Unavailable,
                $"Duplicate-therapy check unavailable — {u.Reason}.",
                $"تعذّر التحقق من ازدواج العلاج — {u.Reason}.",
                provenance: null))];
        }

        var available = (Fetched<IReadOnlyDictionary<Guid, DrugComposition>>.Available)compositions;
        var byDrug = available.Value;
        var provenance = available.Provenance;

        var findings = new List<Finding>();
        var flagged = new HashSet<Guid>();
        var unresolved = new HashSet<Guid>();

        for (var i = 0; i < request.Lines.Count; i++)
        {
            for (var j = i + 1; j < request.Lines.Count; j++)
            {
                var a = request.Lines[i];
                var b = request.Lines[j];

                byDrug.TryGetValue(a.DrugId, out var ca);
                byDrug.TryGetValue(b.DrugId, out var cb);

                // A product whose molecules the catalogue never recorded cannot be compared. Noted per line
                // and reported below, rather than letting the pair pass as "no duplication found".
                if (ca is null || ca.IngredientKeys.Count == 0) unresolved.Add(a.LineId);
                if (cb is null || cb.IngredientKeys.Count == 0) unresolved.Add(b.LineId);

                // THE SAME PRODUCT TWICE. Phase 26 skipped exactly this line.
                if (a.DrugId == b.DrugId)
                {
                    findings.Add(Duplicate(a, b, provenance,
                        $"{b.DrugName} appears on this prescription twice",
                        $"{b.DrugName} مكرر في هذه الوصفة",
                        ClinicalSeverity.Major));
                    findings.Add(Duplicate(b, a, provenance,
                        $"{a.DrugName} appears on this prescription twice",
                        $"{a.DrugName} مكرر في هذه الوصفة",
                        ClinicalSeverity.Major));
                    flagged.Add(a.LineId);
                    flagged.Add(b.LineId);
                    continue;
                }

                if (ca is null || cb is null) continue;

                var shared = ca.IngredientKeys
                    .Intersect(cb.IngredientKeys, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();

                if (shared.Count > 0)
                {
                    var molecules = string.Join(", ", shared);

                    // Major, not Moderate: the same molecule reaching a patient twice is a dosing question,
                    // and the prescriber has to look at the combined daily total before proceeding.
                    findings.Add(Duplicate(a, b, provenance,
                        $"both this and {b.DrugName} contain {molecules} — check the combined daily total",
                        $"يحتوي هذا الدواء و{b.DrugName} على {molecules} — يُرجى مراجعة الإجمالي اليومي المشترك",
                        ClinicalSeverity.Major));
                    findings.Add(Duplicate(b, a, provenance,
                        $"both this and {a.DrugName} contain {molecules} — check the combined daily total",
                        $"يحتوي هذا الدواء و{a.DrugName} على {molecules} — يُرجى مراجعة الإجمالي اليومي المشترك",
                        ClinicalSeverity.Major));
                    flagged.Add(a.LineId);
                    flagged.Add(b.LineId);
                    continue;
                }

                // Same therapeutic class, different molecules: two NSAIDs, two PPIs, two SSRIs. Moderate —
                // it is worth seeing and is sometimes deliberate, so it renders inline and does not gate.
                if (ca.AtcClass4 is { Length: > 0 } cls
                    && string.Equals(cls, cb.AtcClass4, StringComparison.Ordinal))
                {
                    findings.Add(Duplicate(a, b, provenance,
                        $"this and {b.DrugName} are both in ATC class {cls} — check whether both are intended",
                        $"هذا الدواء و{b.DrugName} من الفئة الدوائية {cls} — يُرجى التأكد من قصد وصفهما معًا",
                        ClinicalSeverity.Moderate));
                    findings.Add(Duplicate(b, a, provenance,
                        $"this and {a.DrugName} are both in ATC class {cls} — check whether both are intended",
                        $"هذا الدواء و{a.DrugName} من الفئة الدوائية {cls} — يُرجى التأكد من قصد وصفهما معًا",
                        ClinicalSeverity.Moderate));
                    flagged.Add(a.LineId);
                    flagged.Add(b.LineId);
                }
            }
        }

        foreach (var line in request.Lines.Where(l => !flagged.Contains(l.LineId)))
        {
            // An unresolvable product genuinely was not compared. Saying "no duplication" about a medicine
            // whose molecules we do not know is the false assurance this whole phase exists to remove.
            findings.Add(unresolved.Contains(line.LineId)
                ? Finding.Clinical(
                    line.LineId, line.DrugId, CheckKind.Duplication, ClinicalState.NotChecked,
                    $"Not checked — no active ingredient is recorded for {line.DrugName}, so it could not be "
                    + "compared with the other medicines on this prescription.",
                    $"لم يتم التحقق — لا توجد مادة فعالة مسجلة لـ {line.DrugName}، لذلك تعذّرت مقارنته ببقية "
                    + "أدوية هذه الوصفة.",
                    provenance)
                : Finding.Clinical(
                    line.LineId, line.DrugId, CheckKind.Duplication, ClinicalState.Ok,
                    "No duplicate therapy on this prescription.",
                    "لا يوجد ازدواج علاجي في هذه الوصفة.",
                    provenance));
        }

        return findings;
    }

    private static Finding Duplicate(
        PrescriptionLineInput line, PrescriptionLineInput other, ProvenanceInfo provenance,
        string en, string ar, ClinicalSeverity severity)
        => Finding.Clinical(
            line.LineId, line.DrugId, CheckKind.Duplication, ClinicalState.Warning,
            $"Duplicate therapy: {en}.", $"ازدواج علاجي: {ar}.",
            provenance, severity, relatedLineId: other.LineId);
}

/// <summary>
/// The medicine against conditions the patient actually has (28.9, doc 44 §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not the indication check.</b> Indication asks whether the drug is USED FOR the recorded
/// diagnosis; a mismatch means off-label, which is legitimate, common, and therefore a warning that fires on
/// a large share of correct prescriptions. This asks whether the drug is DANGEROUS IN the recorded
/// condition, which produces a finding only when there is harm to avoid.
/// </para>
/// <para>
/// The distinction is the whole point. Design 44 §5 identifies conflating them as the reason the first check
/// is dismissed — and a dismissed check buries the second one inside it.
/// </para>
/// </remarks>
internal static class ContraindicationChecks
{
    internal static Finding Evaluate(
        PrescriptionLineInput line, Fetched<ContraindicationTable> contraindications)
    {
        if (contraindications is Fetched<ContraindicationTable>.Unavailable u)
        {
            return Clinical(line, ClinicalState.Unavailable,
                $"Contraindication check unavailable — {u.Reason}.",
                $"تعذّر التحقق من موانع الاستعمال — {u.Reason}.",
                provenance: null);
        }

        var available = (Fetched<ContraindicationTable>.Available)contraindications;
        var table = available.Value;
        var provenance = available.Provenance;

        // An empty rule list finds nothing, which is not the same as there being nothing to find.
        if (table.KnownRuleCount == 0)
        {
            return Clinical(line, ClinicalState.NotChecked,
                "Not checked — Mersal's contraindication list contains 0 rules.",
                "لم يتم التحقق — قائمة موانع الاستعمال لدى مرسال لا تحتوي على أي قواعد.",
                provenance);
        }

        // The most serious rule that fired. Several can — an NSAID in a patient with both peptic ulcer
        // disease and chronic kidney disease is two rules — and the prescriber needs the worst one first.
        var hit = table.Hits.Where(h => h.DrugId == line.DrugId)
            .OrderByDescending(h => h.Severity).FirstOrDefault();

        if (hit is null)
        {
            return Clinical(line, ClinicalState.Ok,
                $"No contraindication found against the recorded conditions "
                + $"(checked against {table.KnownRuleCount} rules).",
                $"لم يُعثر على موانع استعمال مقابل الحالات المسجلة "
                + $"(تم التحقق مقابل {table.KnownRuleCount} قاعدة).",
                provenance);
        }

        return Finding.Clinical(
            line.LineId, line.DrugId, CheckKind.Contraindication, ClinicalState.Warning,
            $"This medicine {hit.MatchedOn}. {hit.MechanismEn} {hit.ClinicalEffectEn} {hit.ManagementEn}",
            $"هذا الدواء {hit.MatchedOn}. {hit.MechanismAr} {hit.ClinicalEffectAr} {hit.ManagementAr}",
            provenance, hit.Severity, relatedLineId: null, referenceText: hit.Citation);
    }

    private static Finding Clinical(
        PrescriptionLineInput line, ClinicalState state, string en, string ar, ProvenanceInfo? provenance)
        => Finding.Clinical(line.LineId, line.DrugId, CheckKind.Contraindication, state, en, ar, provenance);
}

/// <summary>
/// 29.6 — how much to dispense, and the far more important case of not being able to say (design 45 §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant 8: missing unit data yields NotChecked NAMING the field, never a guessed quantity.</b> The
/// quantity is not a convenience — it is what the pharmacy dispenses. A silently wrong one is a dispensing
/// error, and the two ways to produce one are both defaults: assuming a pack splits (which permits a
/// fractional inhaler) and assuming it does not (which rounds a tablet count up to whole boxes). So absence
/// is reported as itself, and the MISSING COLUMN is named — because the person who resolves it edits the
/// drug table, and "quantity could not be computed" alone sends a prescriber to guess instead.
/// </para>
/// <para>
/// <b>Rounding, where it happens at all, is UP and only for a pack that cannot be broken.</b> Rounding down
/// sends a patient home short of the course they were prescribed.
/// </para>
/// </remarks>
internal static class QuantityChecks
{
    internal static Finding Evaluate(
        PrescriptionLineInput line, Fetched<IReadOnlyDictionary<Guid, DrugPackFacts>> packFacts)
    {
        if (packFacts is Fetched<IReadOnlyDictionary<Guid, DrugPackFacts>>.Unavailable u)
        {
            // The SOURCE was down. Not the same fact as "the data is missing", and the prescriber deciding
            // whether to chase a master-data fix is the one who needs the difference.
            return Clinical(line, ClinicalState.Unavailable,
                $"Quantity check unavailable — {u.Reason}.",
                $"تعذّر التحقق من الكمية — {u.Reason}.",
                provenance: null);
        }

        var available = (Fetched<IReadOnlyDictionary<Guid, DrugPackFacts>>.Available)packFacts;
        var provenance = available.Provenance;

        // Nothing to compute FROM. The commonest case on a free-text dose, and it is stated rather than
        // passed silently — an Ok here would read as "the quantity is right".
        if (line.DoseAmount is null || line.TimesPerDay is null || line.DurationDays is null)
        {
            return Clinical(line, ClinicalState.NotChecked,
                "Not checked — this line has no numeric dose, frequency and duration to compute a quantity from.",
                "لم يتم التحقق — لا تحتوي هذه السطر على جرعة رقمية وتكرار ومدة لحساب الكمية منها.",
                provenance);
        }

        var days = line.DurationDays.Value;

        if (!available.Value.TryGetValue(line.DrugId, out var pack))
        {
            // No row at all is not a row of tidy defaults. 2,495 products in the catalogue are in this state.
            return Clinical(line, ClinicalState.NotChecked,
                "Not checked — master data records no pack information for this drug "
                + "('is_pack_splittable', 'pack_size').",
                "لم يتم التحقق — لا تسجّل البيانات المرجعية معلومات العبوة لهذا الدواء "
                + "('is_pack_splittable', 'pack_size').",
                provenance);
        }

        /*
         * THE ARITHMETIC IS NOT DONE HERE.
         *
         * It moved to `Mersal.Prescribing.QuantityMath` because the composer needs the same NUMBER while the
         * doctor is still typing, and all this check ever exposed was a formatted sentence. A composer that
         * re-derived it in TypeScript would be a second implementation of the one division that decides how
         * much medicine a person is handed — and the two would be discovered to disagree at a counter.
         *
         * What stays here is the JUDGEMENT: turning an outcome into a five-state finding, with the missing
         * field named in the words a data administrator can act on.
         */
        var outcome = QuantityMath.Compute(
            line.DoseAmount, line.TimesPerDay, line.DurationDays, pack.IsPackSplittable, pack.PackContent);

        if (outcome.Plan is not { } plan)
        {
            var (en, ar) = outcome.MissingField switch
            {
                "is_pack_splittable" => (
                    "Not checked — master data does not record 'is_pack_splittable' for this drug, so its "
                    + "quantity cannot be computed. A guessed quantity is a dispensing error.",
                    "لم يتم التحقق — لا تسجّل البيانات المرجعية 'is_pack_splittable' لهذا الدواء، لذلك يتعذّر "
                    + "حساب الكمية. الكمية المُخمّنة خطأ في الصرف."),
                // 31.3 — the column NAMED is the one a data administrator has to fill: for a syrup it is
                // the workbook's "Volume / Weight", which is what pack_content is derived from. `pack_size`
                // is populated on this row and is not the number that was missing.
                "pack_content" => (
                    "Not checked — this pack cannot be split and master data does not record 'pack_content' "
                    + "for this drug (how much one box holds), so the number of whole packs cannot be "
                    + "computed.",
                    "لم يتم التحقق — لا يمكن تجزئة هذه العبوة ولا تسجّل البيانات المرجعية 'pack_content' "
                    + "لهذا الدواء (سعة العبوة الواحدة)، لذلك يتعذّر حساب عدد العبوات الكاملة."),
                _ => (
                    "Not checked — this line has no numeric dose, frequency and duration to compute a "
                    + "quantity from.",
                    "لم يتم التحقق — لا تحتوي هذه السطر على جرعة رقمية وتكرار ومدة لحساب الكمية منها."),
            };
            return Clinical(line, ClinicalState.NotChecked, en, ar, provenance);
        }

        if (plan.Packs is { } packs)
        {
            return Clinical(line, ClinicalState.Ok,
                $"{plan.TotalUnits:0.##} units required — {packs:0} whole pack(s) of {plan.PackContent:0.##} "
                + $"({plan.DispenseQuantity:0.##} units). This pack cannot be split.",
                $"المطلوب {plan.TotalUnits:0.##} وحدة — {packs:0} عبوة كاملة سعة {plan.PackContent:0.##} "
                + $"({plan.DispenseQuantity:0.##} وحدة). لا يمكن تجزئة هذه العبوة.",
                provenance);
        }

        return Clinical(line, ClinicalState.Ok,
            $"{plan.TotalUnits:0.##} units required over {days} day(s).",
            $"المطلوب {plan.TotalUnits:0.##} وحدة على مدى {days} يوم.",
            provenance);
    }

    private static Finding Clinical(
        PrescriptionLineInput line, ClinicalState state, string en, string ar, ProvenanceInfo? provenance)
        => Finding.Clinical(line.LineId, line.DrugId, CheckKind.Quantity, state, en, ar, provenance);
}
