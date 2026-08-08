using FluentAssertions;

namespace Mersal.ClinicalValidation.Tests;

/// <summary>
/// The prescribing validation engine (phase 26.3, doc 43 §1/§6).
/// </summary>
/// <remarks>
/// Two invariants dominate every test here: a clinical check may warn but never block, and a check whose
/// data source failed must never render as OK. The second is the reason the phase exists — before it,
/// pharmacy's screener turned every transport error, and every non-2xx response, into "no alerts".
/// </remarks>
public class PrescriptionValidatorTests
{
    // ================================================================= indication ↔ diagnosis

    [Fact]
    public void A_recorded_diagnosis_in_the_drugs_indication_set_is_Ok()
    {
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11", "E10"]))), diagnoses: ["E11.9"]);

        result.For(line.LineId, CheckKind.Indication).State.Should().Be(CheckState.Ok);
    }

    [Fact]
    public void A_specific_diagnosis_matches_a_category_level_indication()
    {
        // The finding that shaped the loader: indications are recorded as 3-character categories ("E11"),
        // diagnoses as specific codes ("E11.9"). Comparing them by equality would warn on virtually every
        // prescription, and a warning that always fires is one clinicians learn to click through.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"]))), diagnoses: ["E11.9"]);

        result.For(line.LineId, CheckKind.Indication).State.Should().Be(CheckState.Ok);
    }

    [Fact]
    public void An_unlisted_indication_WARNS_and_never_blocks()
    {
        // Off-label prescribing is legitimate and common. This is a warning forever.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"]))), diagnoses: ["J01.0"]);

        var finding = result.For(line.LineId, CheckKind.Indication);
        finding.State.Should().Be(CheckState.Warning);
        finding.IsBlocking.Should().BeFalse();
        finding.RequiresAcknowledgement.Should().BeTrue();
    }

    [Fact]
    public void A_drug_with_no_indication_data_is_NotChecked_not_a_mismatch()
    {
        // 1,019 real products are in this state — their only listed code was the Z76 filler. Reporting them
        // as a mismatch would be a false negative; reporting them as OK would be a false assurance.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(indications: Fx.Indications(new DrugIndicationFact(line.DrugId, []))), diagnoses: ["E11.9"]);

        var finding = result.For(line.LineId, CheckKind.Indication);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.MessageEn.Should().Contain("no indication data");
    }

    [Fact]
    public void A_drug_absent_from_the_indication_set_entirely_is_NotChecked()
    {
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(), diagnoses: ["E11.9"]);

        result.For(line.LineId, CheckKind.Indication).State.Should().Be(CheckState.NotChecked);
    }

    [Fact]
    public void With_no_diagnosis_recorded_the_check_says_so_and_does_NOT_report_Ok()
    {
        // Doc 43 §6: the check has nothing to compare against, so it reports "no diagnosis recorded".
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"]))), diagnoses: []);

        var finding = result.For(line.LineId, CheckKind.Indication);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("no diagnosis recorded");
    }

    /*
     * 28.7 — the hierarchy walk (doc 44 §6). Truncation handled the common case and could not express the
     * rest: a BLOCK-level indication is not three characters, an indication more specific than three
     * characters was silently widened to its whole category, and a less-specific diagnosis read as a
     * mismatch when it is an open question.
     */

    [Fact]
    public void A_BLOCK_LEVEL_INDICATION_MATCHES_A_CODE_UNDERNEATH_IT()
    {
        // "J00-J06" is acute upper respiratory infections — a BLOCK, not a category. Truncation cannot
        // express it at all, which is why the hierarchy had to be loaded rather than derived.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["J00-J06"])),
                diagnoses: Fx.Diagnoses(["J01.0"], ancestors: Fx.Hierarchy(("J01.0", ["J01", "J00-J06", "X"])))));

        result.For(line.LineId, CheckKind.Indication).State.Should().Be(CheckState.Ok);
    }

    [Fact]
    public void A_LESS_SPECIFIC_DIAGNOSIS_IS_A_POSSIBLE_MATCH_NOT_A_MISS_AND_NOT_A_HIT()
    {
        // Diagnosis "E11", indication "E11.9". The patient may well have the more specific condition, but
        // nobody has coded it that way. Calling it a hit asserts something the record does not say; calling
        // it a miss warns off-label on a prescription that is very likely on-label.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                indications: Fx.Indications(new DrugIndicationFact(
                    line.DrugId, ["E11.9"], Fx.Hierarchy(("E11.9", ["E11", "E10-E14", "IV"])))),
                diagnoses: Fx.Diagnoses(["E11"], ancestors: Fx.Hierarchy(("E11", ["E10-E14", "IV"])))));

        var finding = result.For(line.LineId, CheckKind.Indication);
        finding.State.Should().Be(CheckState.NotChecked, "it is neither a clean hit nor a mismatch");
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("less specific");
    }

    [Fact]
    public void With_no_hierarchy_loaded_the_check_falls_back_to_the_category_comparison()
    {
        // A catalogue that has not been reloaded since the closure table arrived has no ancestor rows. The
        // fallback is what stops that from warning off-label on every prescription in the meantime.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"])),
                diagnoses: Fx.Diagnoses(["E11.9"])));

        result.For(line.LineId, CheckKind.Indication).State.Should().Be(CheckState.Ok);
    }

    [Fact]
    public void A_dotted_and_an_undotted_code_are_the_same_code()
    {
        // "E119" is how some sources write "E11.9". One normaliser, one answer — the whole point of
        // deleting the second implementation.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"])),
                diagnoses: Fx.Diagnoses(["E119"])));

        result.For(line.LineId, CheckKind.Indication).State.Should().Be(CheckState.Ok);
    }

    // ================================================================= drug–drug interaction

    [Fact]
    public void An_interacting_pair_across_two_lines_warns_on_BOTH_lines()
    {
        var a = Fx.Line(name: "Drug A");
        var b = Fx.Line(name: "Drug B");
        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(interactions: Fx.Interactions(50,
                new InteractionFact(a.DrugId, b.DrugId, ClinicalSeverity.Major, "Additive toxicity"))));

        result.For(a.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Warning);
        result.For(b.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Warning);
        result.For(a.LineId, CheckKind.Interaction).Severity.Should().Be(ClinicalSeverity.Major);
        result.For(a.LineId, CheckKind.Interaction).RelatedLineId.Should().Be(b.LineId);
    }

    [Fact]
    public void A_pair_recorded_in_the_opposite_order_still_matches()
    {
        // The source stores an unordered pair once, in whichever order it happened to be written.
        var a = Fx.Line();
        var b = Fx.Line();
        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(interactions: Fx.Interactions(50,
                new InteractionFact(b.DrugId, a.DrugId, ClinicalSeverity.Moderate, null))));

        result.For(a.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Warning);
    }

    [Fact]
    public void Every_unordered_pair_is_considered_once()
    {
        // n lines → n(n-1)/2 pairs. Four mutually interacting drugs is 6 pairs, so 12 findings (each pair
        // reported on both of its lines) — not 16, and not 6.
        var lines = Enumerable.Range(0, 4).Select(i => Fx.Line(name: $"Drug {i}")).ToList();
        var pairs = new List<InteractionFact>();
        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = i + 1; j < lines.Count; j++)
            {
                pairs.Add(new InteractionFact(lines[i].DrugId, lines[j].DrugId, ClinicalSeverity.Minor, null));
            }
        }

        var result = Fx.Run(Fx.Request(lines), Fx.Snapshot(interactions: Fx.Interactions(50, [.. pairs])));

        pairs.Should().HaveCount(6, "4 lines yield 4*3/2 pairs");
        result.Findings.Count(f => f.Kind == CheckKind.Interaction && f.State == CheckState.Warning)
            .Should().Be(12);
    }

    [Fact]
    public void A_line_is_checked_against_medications_the_patient_already_takes()
    {
        var line = Fx.Line();
        var existing = Guid.NewGuid();
        var result = Fx.Run(
            Fx.Request([line], activeMedications: [existing]),
            Fx.Snapshot(interactions: Fx.Interactions(50,
                new InteractionFact(line.DrugId, existing, ClinicalSeverity.Contraindicated, null))));

        var finding = result.For(line.LineId, CheckKind.Interaction);
        finding.State.Should().Be(CheckState.Warning);
        finding.Severity.Should().Be(ClinicalSeverity.Contraindicated);
        finding.IsBlocking.Should().BeFalse("even a contraindicated pair warns — clinical checks never block");
    }

    [Fact]
    public void No_interaction_found_against_a_populated_list_is_Ok_and_says_what_it_checked()
    {
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(interactions: Fx.Interactions(knownPairCount: 137)));

        var finding = result.For(line.LineId, CheckKind.Interaction);
        finding.State.Should().Be(CheckState.Ok);
        finding.MessageEn.Should().Contain("137",
            "internal curation means partial coverage, and the UI must state the extent rather than imply completeness");
    }

    [Fact]
    public void An_EMPTY_interaction_list_is_NotChecked_not_Ok()
    {
        // The table exists and is empty. Finding nothing in an empty list is not evidence of safety.
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(interactions: Fx.Interactions(knownPairCount: 0)));

        var finding = result.For(line.LineId, CheckKind.Interaction);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("0 pairs");
    }

    // ================================================================= allergy

    [Fact]
    public void A_drug_conflicting_with_a_recorded_allergy_warns()
    {
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fx.Allergies(2, conflicts: new AllergyConflict(line.DrugId, "penicillin"))));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.State.Should().Be(CheckState.Warning);
        finding.MessageEn.Should().Contain("penicillin");
        finding.IsBlocking.Should().BeFalse();
    }

    [Fact]
    public void No_conflict_against_a_fully_screened_history_is_Ok()
    {
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fx.Allergies(recordedCount: 3, screenedCount: 3)));

        result.For(line.LineId, CheckKind.Allergy).State.Should().Be(CheckState.Ok);
    }

    [Fact]
    public void No_recorded_allergies_is_NotChecked_because_absence_is_not_a_negative_result()
    {
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(allergies: Fx.Allergies(recordedCount: 0)));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
    }

    /*
     * ---------------------------------------------------------------------------------------------------
     * THE DEFECT PHASE 28 EXISTS FOR (doc 44 §1.1).
     *
     * masterdata matched an allergy by testing whether a recorded allergen CODE appeared in the drug's ATC
     * ancestor chain. The seeded codes are ALG-PENICILLIN / ALG-SULFA / ALG-CEPHALO; the chain holds J,
     * J01, J01C. The two sets are disjoint by construction, so the comparison could never be true — and
     * this engine then rendered "No conflict with the 3 recorded allergy/allergies."
     *
     * A false negative presented as a positive assurance is the worst failure shape in clinical decision
     * support, because unlike a missing check it actively reassures. The four tests below are the ones that
     * would have caught it, and none of them existed.
     * ---------------------------------------------------------------------------------------------------
     */

    [Fact]
    public void An_allergen_that_could_not_be_mapped_is_NotChecked_and_is_named()
    {
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fx.Allergies(
                recordedCount: 1, screenedCount: 0, unmapped: ["Penicillins"])));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        // Naming it is the requirement, not merely declining to say Ok. "Not checked" without a subject
        // gives the prescriber nothing to act on and nothing to correct.
        finding.MessageEn.Should().Contain("Penicillins");
        finding.MessageAr.Should().Contain("Penicillins");
    }

    [Fact]
    public void A_partly_mapped_allergy_history_is_NotChecked_not_Ok()
    {
        // Two of three screened, one unmappable. "Checked two of your three allergies and found nothing"
        // rendered as a green tick is the same false assurance in a smaller dose, and invariant 1 admits no
        // dose: no check returns Ok unless it actually evaluated.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fx.Allergies(
                recordedCount: 3, screenedCount: 2, unmapped: ["Iodine / Contrast media"])));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.MessageEn.Should().Contain("Iodine / Contrast media");
    }

    [Fact]
    public void A_conflict_still_warns_when_another_allergen_is_unmapped()
    {
        // The conflict is the answer and it outranks the gap — but the gap is still disclosed, because a
        // prescriber told "conflicts with penicillin" would otherwise assume the rest was screened.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fx.Allergies(
                recordedCount: 2, screenedCount: 1, unmapped: ["Latex"],
                conflicts: new AllergyConflict(line.DrugId, "penicillin"))));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.State.Should().Be(CheckState.Warning);
        finding.MessageEn.Should().Contain("penicillin");
        finding.MessageEn.Should().Contain("Latex");
    }

    [Fact]
    public void A_history_of_only_food_and_environmental_allergies_is_NotChecked()
    {
        // Nothing was unmappable — a peanut allergy is simply not a question about a medicine. But nothing
        // was screened either, so this is not an all-clear on the patient's DRUG allergy history: that
        // history is unrecorded, which is the case the check already refuses to pass.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fx.Allergies(recordedCount: 2, screenedCount: 0)));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("no medicine-related allergy");
    }

    // ================================================================= dose / duration

    [Fact]
    public void A_drug_with_no_dosing_rule_is_NotChecked()
    {
        // The common case, and the honest one — doc 43 §2 rules out deriving a dose from label prose.
        var line = Fx.Line(doseAmount: 500, timesPerDay: 3);
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot());

        var finding = result.For(line.LineId, CheckKind.DoseDuration);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.MessageEn.Should().Contain("no dosing rule");
    }

    [Fact]
    public void A_daily_dose_over_the_configured_maximum_warns()
    {
        var line = Fx.Line(doseAmount: 1000, doseUnit: "mg", timesPerDay: 4);
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(dosingRules: Fx.DosingRules(new DosingRuleFact(line.DrugId, MaxDailyDose: 3000, DoseUnit: "mg"))));

        var finding = result.For(line.LineId, CheckKind.DoseDuration);
        finding.State.Should().Be(CheckState.Warning);
        finding.MessageEn.Should().Contain("4000");
        finding.IsBlocking.Should().BeFalse();
    }

    [Fact]
    public void A_duration_over_the_configured_ceiling_warns()
    {
        var line = Fx.Line(durationDays: 30);
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(dosingRules: Fx.DosingRules(new DosingRuleFact(line.DrugId, MaxDurationDays: 14))));

        result.For(line.LineId, CheckKind.DoseDuration).State.Should().Be(CheckState.Warning);
    }

    [Fact]
    public void A_dose_within_the_rule_is_Ok()
    {
        var line = Fx.Line(doseAmount: 500, doseUnit: "mg", timesPerDay: 2, durationDays: 7);
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(dosingRules: Fx.DosingRules(
                new DosingRuleFact(line.DrugId, MaxDailyDose: 3000, DoseUnit: "mg", MaxDurationDays: 14))));

        result.For(line.LineId, CheckKind.DoseDuration).State.Should().Be(CheckState.Ok);
    }

    // ================================================================= benefit seam

    [Fact]
    public void The_benefit_seam_reports_NotChecked_in_phase_26()
    {
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot());

        result.For(line.LineId, CheckKind.Benefit).State.Should().Be(CheckState.NotChecked);
    }

    [Fact]
    public void A_benefit_rule_MAY_block_and_it_is_the_only_thing_that_can()
    {
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(benefit: Fx.Benefit(new BenefitOutcome(
                line.LineId, BenefitState.Blocked, "Outside the UNHCR formulary.", "خارج قائمة الأدوية."))));

        result.For(line.LineId, CheckKind.Benefit).IsBlocking.Should().BeTrue();
        result.StateFor(line.LineId).Should().Be(CheckState.Blocked);
        result.HasBlocking.Should().BeTrue();
    }

    // ================================================================= THE invariants

    [Fact]
    public void A_DEAD_DEPENDENCY_YIELDS_UNAVAILABLE_EVERYWHERE_AND_NEVER_OK()
    {
        // The test this whole phase exists for. Before phase 26 an outage rendered as a clean bill of health,
        // because every transport error and every non-2xx was swallowed into "no alerts".
        var lines = new[] { Fx.Line(), Fx.Line() };
        var result = Fx.Run(Fx.Request(lines), Fx.DeadSnapshot(), diagnoses: ["E11.9"]);

        result.Findings.Should().NotBeEmpty();
        result.Findings.Should().OnlyContain(f => f.State == CheckState.Unavailable);
        result.Findings.Should().NotContain(f => f.State == CheckState.Ok);

        foreach (var line in lines)
        {
            result.StateFor(line.LineId).Should().Be(CheckState.Unavailable);
        }
    }

    [Fact]
    public void Each_check_reports_Unavailable_independently_of_the_others()
    {
        // A partial outage must not be rounded to "fine" on the strength of the checks that did run.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"])),
                interactions: Fetched.NotAvailable<InteractionTable>("masterdata timed out")), diagnoses: ["E11.9"]);

        result.For(line.LineId, CheckKind.Indication).State.Should().Be(CheckState.Ok);
        result.For(line.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Unavailable);
        result.StateFor(line.LineId).Should().Be(CheckState.Unavailable,
            "a line with an unchecked source is not adequately described by its checks that did run");
    }

    [Fact]
    public void Unavailable_outranks_Warning_when_summarising_a_line()
    {
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"])),  // → Warning
                allergies: Fetched.NotAvailable<AllergyScreen>("emr unreachable")), diagnoses: ["J01.0"]);        // → Unavailable

        result.For(line.LineId, CheckKind.Indication).State.Should().Be(CheckState.Warning);
        result.StateFor(line.LineId).Should().Be(CheckState.Unavailable,
            "otherwise the prescriber has no way to see that something went unchecked");
    }

    /*
     * ================================================================= severity tiering (28.4, doc 44 §2)
     *
     * Every clinical finding used to be a Warning requiring a typed acknowledgement, so a contraindicated
     * combination and a trivial one demanded the same click. That is the single best-documented failure mode
     * in clinical decision support: override rates above 90% are routinely reported, and the mechanism is
     * always this one — when everything interrupts, everything gets dismissed, including the alerts worth
     * stopping for.
     *
     * The tier changes INTERRUPTION, never blocking. NO_CLINICAL_CHECK_CAN_EVER_BLOCK below is unaffected
     * and must stay that way.
     */

    [Theory]
    [InlineData(ClinicalSeverity.Contraindicated, true, true)]
    [InlineData(ClinicalSeverity.Major, true, false)]
    [InlineData(ClinicalSeverity.Moderate, false, false)]
    [InlineData(ClinicalSeverity.Minor, false, false)]
    public void Only_Major_and_Contraindicated_gate_submission(
        ClinicalSeverity severity, bool gates, bool needsTypedReason)
    {
        var a = Fx.Line(name: "Drug A");
        var b = Fx.Line(name: "Drug B");
        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(interactions: Fx.Interactions(50, new InteractionFact(a.DrugId, b.DrugId, severity, null))));

        var finding = result.For(a.LineId, CheckKind.Interaction);

        finding.State.Should().Be(CheckState.Warning, "the tier changes interruption, not the state");
        finding.RequiresAcknowledgement.Should().Be(gates);
        finding.RequiresTypedReason.Should().Be(needsTypedReason);
        finding.IsBlocking.Should().BeFalse("severity never turns a clinical check into a refusal");
    }

    [Fact]
    public void A_moderate_finding_leaves_the_line_submittable()
    {
        // The whole point of the tier, stated at the level the UI reads: a Moderate interaction is shown
        // beside the line and does not stand between the prescriber and Submit.
        var a = Fx.Line();
        var b = Fx.Line();
        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(interactions: Fx.Interactions(50,
                new InteractionFact(a.DrugId, b.DrugId, ClinicalSeverity.Moderate, "Additive drowsiness"))));

        result.UnacknowledgedBlockers(a.LineId).Should().BeEmpty();
        result.StateFor(a.LineId).Should().Be(CheckState.Warning, "it is still visible — it just does not gate");
    }

    [Fact]
    public void An_ungraded_finding_still_interrupts()
    {
        // Manufacturer-label interactions carry no severity: a label states an effect, not a rank. Treating
        // "ungraded" as "not serious" would be the engine inventing a clinical judgement it has no source
        // for — and it would silence the only interaction source the platform had while the curated list
        // was empty.
        var a = Fx.Line(name: "Warfarin");
        var b = Fx.Line(name: "Ibuprofen");
        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(labels: Fx.Labels(
                Fx.Label(a.DrugId, "warfarin", interactions: "Concomitant ibuprofen increases bleeding risk."),
                Fx.Label(b.DrugId, "ibuprofen"))));

        var finding = result.LabelFor(a.LineId, CheckKind.Interaction);
        finding.Severity.Should().BeNull();
        finding.RequiresAcknowledgement.Should().BeTrue();
    }

    [Fact]
    public void An_allergy_conflict_carries_its_severity_and_its_management_line()
    {
        // Design 44 §3/§6: the management line is the field most likely to change the prescription, so it
        // travels in the message rather than in a collapsed disclosure.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fx.Allergies(1, conflicts: new AllergyConflict(
                line.DrugId,
                "possible cross-reaction (low confidence) between ATC class J01D and the recorded allergy to Penicillins",
                ClinicalSeverity.Moderate, "Low",
                ManagementEn: "Cross-reactivity without a shared side chain is low; weigh the recorded reaction.",
                ManagementAr: "التفاعل المتبادل دون سلسلة جانبية مشتركة منخفض.",
                Citation: "Picard 2019"))));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.State.Should().Be(CheckState.Warning);
        finding.Severity.Should().Be(ClinicalSeverity.Moderate);
        finding.MessageEn.Should().Contain("low confidence").And.Contain("shared side chain");
        finding.MessageAr.Should().Contain("منخفض");
        // A low-confidence inference must not demand the same click as the allergy itself: blanket
        // cephalosporin avoidance after a penicillin label causes real harm through worse antibiotic choice.
        finding.RequiresAcknowledgement.Should().BeFalse();
    }

    [Fact]
    public void A_direct_allergy_match_is_Major_and_does_gate()
    {
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fx.Allergies(1, conflicts: new AllergyConflict(
                line.DrugId, "contains amoxicillin, which is covered by the recorded allergy to Penicillins"))));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.Severity.Should().Be(ClinicalSeverity.Major);
        finding.RequiresAcknowledgement.Should().BeTrue();
        finding.RequiresTypedReason.Should().BeFalse("a typed reason is reserved for Contraindicated");
    }

    [Fact]
    public void A_medicine_with_no_ingredient_or_ATC_reports_the_DRUGS_gap_not_the_patients()
    {
        // 4.7% of catalogue products have no recorded active ingredient and 14.8% no ATC code. A product
        // with neither cannot be compared with any allergy — and the prescriber must be sent to the
        // catalogue row, not to the patient's allergy history.
        var line = Fx.Line(name: "Unclassified syrup");
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(allergies: Fetched.From(
                new AllergyScreen([], 3, [], 0, new HashSet<Guid> { line.DrugId }), Fx.Provenance)));

        var finding = result.For(line.LineId, CheckKind.Allergy);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("no active ingredient or drug class is recorded");
        finding.MessageEn.Should().Contain("Unclassified syrup");
    }

    /*
     * ================================================================= duplicate therapy (28.5, doc 44 §7)
     *
     * Phase 26 skipped it in one line — `if (a.DrugId == b.DrugId) continue;` — and nothing anywhere compared
     * two DIFFERENT products for a shared molecule. Two trade names holding one molecule is the commonest
     * real prescribing duplication, and it is only detectable because products now decompose.
     */

    [Fact]
    public void TWO_BRANDS_OF_PARACETAMOL_WARN_INCLUDING_WHEN_ONE_HIDES_IN_A_COMBINATION()
    {
        // The classic accidental overdose. Paracetamol for the fever, a cold-and-flu compound for the
        // symptoms, and nothing on either box says the combined daily total crosses the hepatotoxic ceiling.
        var panadol = Fx.Line(name: "Panadol 500mg");
        var coldFlu = Fx.Line(name: "Cold & Flu Day");

        var result = Fx.Run(
            Fx.Request([panadol, coldFlu]),
            Fx.Snapshot(compositions: Fx.Compositions(
                new DrugComposition(panadol.DrugId, ["paracetamol"], "N02B"),
                new DrugComposition(coldFlu.DrugId, ["paracetamol", "phenylephrine", "chlorphenamine"], "R05X"))));

        foreach (var line in new[] { panadol, coldFlu })
        {
            var finding = result.For(line.LineId, CheckKind.Duplication);
            finding.State.Should().Be(CheckState.Warning);
            finding.Severity.Should().Be(ClinicalSeverity.Major);
            finding.MessageEn.Should().Contain("paracetamol").And.Contain("combined daily total");
            finding.IsBlocking.Should().BeFalse();
        }

        result.For(panadol.LineId, CheckKind.Duplication).RelatedLineId.Should().Be(coldFlu.LineId);
    }

    [Fact]
    public void The_same_product_twice_warns_instead_of_being_silently_skipped()
    {
        // The literal line phase 26 skipped.
        var drugId = Guid.NewGuid();
        var a = Fx.Line(drugId: drugId, name: "Augmentin 1g");
        var b = Fx.Line(drugId: drugId, name: "Augmentin 1g");

        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(compositions: Fx.Compositions(
                new DrugComposition(drugId, ["amoxicillin", "clavulanic acid"], "J01C"))));

        result.For(a.LineId, CheckKind.Duplication).State.Should().Be(CheckState.Warning);
        result.For(a.LineId, CheckKind.Duplication).MessageEn.Should().Contain("twice");
    }

    [Fact]
    public void Two_NSAIDs_warn_at_class_level_and_do_not_gate()
    {
        // Different molecules, same ATC-4. Worth seeing and sometimes deliberate — a cross-taper, a loading
        // dose beside maintenance — so it renders inline rather than standing between the prescriber and
        // Submit.
        var ibuprofen = Fx.Line(name: "Brufen 400mg");
        var diclofenac = Fx.Line(name: "Voltaren 50mg");

        var result = Fx.Run(
            Fx.Request([ibuprofen, diclofenac]),
            Fx.Snapshot(compositions: Fx.Compositions(
                new DrugComposition(ibuprofen.DrugId, ["ibuprofen"], "M01A"),
                new DrugComposition(diclofenac.DrugId, ["diclofenac"], "M01A"))));

        var finding = result.For(ibuprofen.LineId, CheckKind.Duplication);
        finding.State.Should().Be(CheckState.Warning);
        finding.Severity.Should().Be(ClinicalSeverity.Moderate);
        finding.RequiresAcknowledgement.Should().BeFalse();
        finding.MessageEn.Should().Contain("M01A");
    }

    [Fact]
    public void Unrelated_medicines_report_Ok_and_a_single_line_reports_nothing()
    {
        var a = Fx.Line(name: "Metformin");
        var b = Fx.Line(name: "Loratadine");

        var pair = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(compositions: Fx.Compositions(
                new DrugComposition(a.DrugId, ["metformin"], "A10B"),
                new DrugComposition(b.DrugId, ["loratadine"], "R06A"))));
        pair.For(a.LineId, CheckKind.Duplication).State.Should().Be(CheckState.Ok);

        // A single-line prescription has nothing to duplicate. Parking every one of them in "not checked"
        // would drain the meaning out of that state where something really was skipped.
        var alone = Fx.Run(Fx.Request([a]), Fx.Snapshot());
        alone.Findings.Should().NotContain(f => f.Kind == CheckKind.Duplication);
    }

    [Fact]
    public void A_medicine_with_no_recorded_ingredient_is_NotChecked_for_duplication()
    {
        // 4.7% of the catalogue has no recorded active ingredient. Saying "no duplication" about a medicine
        // whose molecules we do not know is the false assurance this phase exists to remove.
        var known = Fx.Line(name: "Panadol 500mg");
        var unknown = Fx.Line(name: "Unclassified syrup");

        var result = Fx.Run(
            Fx.Request([known, unknown]),
            Fx.Snapshot(compositions: Fx.Compositions(
                new DrugComposition(known.DrugId, ["paracetamol"], "N02B"),
                new DrugComposition(unknown.DrugId, [], null))));

        var finding = result.For(unknown.LineId, CheckKind.Duplication);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("no active ingredient is recorded");
    }

    /*
     * ================================================================= patient context (28.8, doc 44 §4)
     *
     * ValidationRequest carried the encounter, the lines, the diagnoses and the active medications — and
     * NOTHING about the patient. A dose check without an age or a weight is an adult fixed-dose check, and
     * this population skews paediatric.
     *
     * The rule that keeps a partial dose check safe: a missing input yields NotChecked NAMING IT, never Ok.
     */

    [Fact]
    public void A_WEIGHT_BASED_RULE_WITH_NO_RECORDED_WEIGHT_IS_NOT_CHECKED_AND_SAYS_WHAT_IS_MISSING()
    {
        var line = Fx.Line(doseAmount: 250, timesPerDay: 3);
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                dosingRules: Fx.DosingRules(new DosingRuleFact(
                    line.DrugId, MaxDailyDose: 1000, DoseUnit: "mg", IsWeightBased: true)),
                patient: Fx.Patient(ageYears: 4, weightKg: null)));

        var finding = result.For(line.LineId, CheckKind.DoseDuration);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("weight is required");
    }

    [Fact]
    public void A_STALE_WEIGHT_IS_NOT_A_CURRENT_WEIGHT()
    {
        // A two-year-old weight on a growing child is worse than none: it produces a confident mg/kg
        // calculation against a number that stopped being true. 30 days for a child, 90 for an adult.
        var line = Fx.Line(doseAmount: 250, timesPerDay: 3);
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                dosingRules: Fx.DosingRules(new DosingRuleFact(
                    line.DrugId, MaxDailyDose: 1000, DoseUnit: "mg", IsWeightBased: true)),
                patient: Fx.Patient(ageYears: 4, weightKg: 16, weighedAt: Fx.RanAt.AddDays(-400))));

        var finding = result.For(line.LineId, CheckKind.DoseDuration);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.MessageEn.Should().Contain("too old to dose against").And.Contain("30 days");
    }

    [Fact]
    public void An_adult_weight_stays_current_for_longer_than_a_childs()
    {
        // Same 60-day-old measurement, two patients. An adult's weight has not meaningfully changed; a
        // child's has.
        var line = Fx.Line(doseAmount: 250, timesPerDay: 3);
        var rule = Fx.DosingRules(new DosingRuleFact(
            line.DrugId, MaxDailyDose: 1000, DoseUnit: "mg", IsWeightBased: true));

        var adult = Fx.Run(Fx.Request([line]), Fx.Snapshot(
            dosingRules: rule, patient: Fx.Patient(ageYears: 40, weightKg: 70, weighedAt: Fx.RanAt.AddDays(-60))));
        var child = Fx.Run(Fx.Request([line]), Fx.Snapshot(
            dosingRules: rule, patient: Fx.Patient(ageYears: 4, weightKg: 16, weighedAt: Fx.RanAt.AddDays(-60))));

        adult.For(line.LineId, CheckKind.DoseDuration).State.Should().Be(CheckState.Ok);
        child.For(line.LineId, CheckKind.DoseDuration).State.Should().Be(CheckState.NotChecked);
    }

    [Fact]
    public void A_RENALLY_CLEARED_DRUG_STATES_THAT_eGFR_IS_UNAVAILABLE_RATHER_THAN_PASSING()
    {
        // The honest limit. Laboratory results are stored as free text, so there is no structured eGFR to
        // adjust against — and a dose check that silently ignores renal clearance on a renally-cleared drug
        // in a patient with kidney disease is worse than no check, because it reassures.
        var line = Fx.Line(doseAmount: 500, timesPerDay: 2);
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(dosingRules: Fx.DosingRules(new DosingRuleFact(
                line.DrugId, MaxDailyDose: 2000, DoseUnit: "mg", RequiresRenalFunction: true))));

        var finding = result.For(line.LineId, CheckKind.DoseDuration);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("eGFR is not available");
    }

    [Fact]
    public void A_dose_within_the_rule_shows_the_recommended_range_and_its_source()
    {
        // The feature design 44 §4 actually asked for, and more useful than a pass/fail: the range informs
        // the override rather than merely obstructing it.
        var line = Fx.Line(doseAmount: 500, timesPerDay: 3);
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(dosingRules: Fx.DosingRules(new DosingRuleFact(
                line.DrugId, MaxDailyDose: 4000, DoseUnit: "mg", TypicalDailyDose: 3000,
                Population: "Adult", Citation: "BNF 87"))));

        var finding = result.For(line.LineId, CheckKind.DoseDuration);
        finding.State.Should().Be(CheckState.Ok);
        finding.MessageEn.Should().Contain("Recommended for Adult").And.Contain("4000mg").And.Contain("BNF 87");
    }

    /*
     * ================================================================= contraindications (28.9, doc 44 §5)
     *
     * "Compatibility of the medication with diagnosis" hides TWO checks, and conflating them is why one is
     * noise. Indication mismatch means off-label — legitimate, common, and dismissed constantly.
     * Contraindication means harm. Phase 26 built the first and not the second.
     */

    private static ContraindicationFact Nsaid(Guid drugId, ClinicalSeverity severity) => new(
        drugId, "is in ATC class M01A, with the recorded diagnosis K27.9", severity,
        "NSAIDs inhibit COX-1 and remove prostaglandin-mediated gastric protection.", "آلية",
        "Re-bleeding or perforation of an existing peptic ulcer.", "أثر",
        "Do not prescribe. Use paracetamol for analgesia.", "لا يُوصف. يُستخدم الباراسيتامول.",
        "NICE CG184");

    [Fact]
    public void AN_NSAID_IN_PEPTIC_ULCER_DISEASE_WARNS_WITH_MECHANISM_AND_AN_ALTERNATIVE()
    {
        var line = Fx.Line(name: "Brufen 400mg");
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(contraindications: Fx.Contraindications(
                8, Nsaid(line.DrugId, ClinicalSeverity.Contraindicated))));

        var finding = result.For(line.LineId, CheckKind.Contraindication);
        finding.State.Should().Be(CheckState.Warning);
        finding.Severity.Should().Be(ClinicalSeverity.Contraindicated);
        finding.RequiresTypedReason.Should().BeTrue();
        // Mechanism, consequence and — the field most likely to change the prescription — an alternative.
        finding.MessageEn.Should().Contain("COX-1").And.Contain("perforation").And.Contain("paracetamol");
        finding.MessageAr.Should().Contain("الباراسيتامول");
        finding.IsBlocking.Should().BeFalse("even a contraindication warns — clinical checks never block");
    }

    [Fact]
    public void The_MOST_SERIOUS_rule_is_reported_when_several_fire()
    {
        // An NSAID in a patient with peptic ulcer disease AND chronic kidney disease is two rules. The
        // prescriber needs the worst one first, not whichever the database returned first.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(contraindications: Fx.Contraindications(
                8, Nsaid(line.DrugId, ClinicalSeverity.Major), Nsaid(line.DrugId, ClinicalSeverity.Contraindicated))));

        result.For(line.LineId, CheckKind.Contraindication).Severity
            .Should().Be(ClinicalSeverity.Contraindicated);
    }

    [Fact]
    public void No_contraindication_against_a_populated_list_is_Ok_and_says_what_it_checked()
    {
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(contraindications: Fx.Contraindications(8)));

        var finding = result.For(line.LineId, CheckKind.Contraindication);
        finding.State.Should().Be(CheckState.Ok);
        finding.MessageEn.Should().Contain("8 rules", "coverage is stated, never implied");
    }

    [Fact]
    public void An_EMPTY_contraindication_list_is_NotChecked_not_Ok()
    {
        // Finding nothing in an empty list is not evidence of safety — the same rule the interaction list
        // has followed since phase 26.
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(contraindications: Fx.Contraindications(0)));

        var finding = result.For(line.LineId, CheckKind.Contraindication);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("0 rules");
    }

    [Fact]
    public void When_the_conditions_could_not_be_read_the_check_is_Unavailable_and_never_Ok()
    {
        // A contraindication is a question about the patient's conditions. If those could not be read,
        // reporting "no contraindication found" would be a clean bill of health on an unknown patient.
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(contraindications:
                Fetched.NotAvailable<ContraindicationTable>("the encounter's conditions could not be read")));

        var finding = result.For(line.LineId, CheckKind.Contraindication);
        finding.State.Should().Be(CheckState.Unavailable);
        finding.State.Should().NotBe(CheckState.Ok);
    }

    [Fact]
    public void NO_CLINICAL_CHECK_CAN_EVER_BLOCK()
    {
        // Exercised across every clinical state a snapshot can produce. The stronger guarantee is structural:
        // clinical checkers are typed to return ClinicalState, which has no Blocked value at all, so writing
        // one is a compile error rather than something review has to catch.
        var line = Fx.Line(doseAmount: 9999, doseUnit: "mg", timesPerDay: 9, durationDays: 999);
        var snapshots = new[]
        {
            Fx.Snapshot(),
            Fx.DeadSnapshot(),
            Fx.Snapshot(
                indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["Z99"])),
                interactions: Fx.Interactions(9, new InteractionFact(line.DrugId, Guid.NewGuid(), ClinicalSeverity.Contraindicated, null)),
                allergies: Fx.Allergies(3, conflicts: new AllergyConflict(line.DrugId, "penicillin")),
                dosingRules: Fx.DosingRules(new DosingRuleFact(line.DrugId, 10, "mg", 1))),
        };

        foreach (var snapshot in snapshots)
        {
            var result = Fx.Run(Fx.Request([line]), snapshot, diagnoses: ["E11.9"]);
            result.Findings.Where(f => f.Kind != CheckKind.Benefit)
                .Should().NotContain(f => f.State == CheckState.Blocked);
        }
    }

    [Fact]
    public void Every_finding_that_had_a_source_carries_its_provenance()
    {
        var line = Fx.Line();
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"]))), diagnoses: ["E11.9"]);

        result.Findings.Should().OnlyContain(f => f.Provenance != null);
        result.Findings.Should().OnlyContain(f =>
            f.Provenance!.SourceName.Length > 0 && f.Provenance.SourceVersion.Length > 0);
    }

    [Fact]
    public void An_unavailable_finding_has_no_provenance_to_claim()
    {
        // There was no source, so attributing one would be a fabrication.
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.DeadSnapshot());

        result.Findings.Should().OnlyContain(f => f.Provenance == null);
    }

    [Fact]
    public void Every_finding_is_bilingual()
    {
        // A finding rendered only in English is a finding not shown to half the platform's users.
        var line = Fx.Line();
        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(), diagnoses: ["E11.9"]);

        result.Findings.Should().OnlyContain(f =>
            !string.IsNullOrWhiteSpace(f.MessageEn) && !string.IsNullOrWhiteSpace(f.MessageAr));
    }

    [Fact]
    public void Every_line_receives_a_verdict_from_every_check()
    {
        var lines = new[] { Fx.Line(), Fx.Line(), Fx.Line() };
        var result = Fx.Run(Fx.Request(lines), Fx.Snapshot(), diagnoses: ["E11.9"]);

        foreach (var line in lines)
        {
            // Distinct kinds: interactions now come from two independent sources — the curated pair list and
            // manufacturer label text — so a line legitimately carries more than one Interaction finding.
            // What must hold is that no KIND is missing.
            result.Findings.Where(f => f.LineId == line.LineId).Select(f => f.Kind).Distinct()
                .Should().BeEquivalentTo(Enum.GetValues<CheckKind>(),
                    "a check that silently skips a line is indistinguishable from one that passed it");
        }
    }

    [Fact]
    public void A_line_with_nothing_to_report_summarises_as_Ok()
    {
        // Guards the guard: if every state collapsed to Unavailable the invariant tests above would pass
        // vacuously. A clean line must actually be able to reach Ok.
        // 29.6 — the line now carries a numeric dose and duration, and the snapshot carries the drug's pack
        // facts. Without them the quantity check reports NotChecked, which is CORRECT and which is exactly
        // why they belong here: "nothing to report" has to mean every check had what it needed, or this
        // guard stops guarding as soon as a new check is added.
        var line = Fx.Line(doseAmount: 500, doseUnit: "mg", timesPerDay: 2, durationDays: 5);
        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                indications: Fx.Indications(new DrugIndicationFact(line.DrugId, ["E11"])),
                interactions: Fx.Interactions(knownPairCount: 40),
                allergies: Fx.Allergies(recordedCount: 2),
                dosingRules: Fx.DosingRules(new DosingRuleFact(line.DrugId, MaxDailyDose: 4000, DoseUnit: "mg")),
                benefit: Fx.Benefit(new BenefitOutcome(line.LineId, BenefitState.Allowed, "Covered.", "مغطى.")),
                packFacts: Fx.PackFacts((line.DrugId, isSplittable: true, packSize: 20m))), diagnoses: ["E11.9"]);

        result.StateFor(line.LineId).Should().Be(CheckState.Ok);
        result.UnacknowledgedBlockers(line.LineId).Should().BeEmpty();
        result.HasBlocking.Should().BeFalse();
    }
}
