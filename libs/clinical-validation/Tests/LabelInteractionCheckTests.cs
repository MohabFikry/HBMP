using FluentAssertions;
using Xunit;

namespace Mersal.ClinicalValidation.Tests;

/// <summary>
/// The interaction check driven by manufacturer label text, across the lines of one prescription.
/// </summary>
/// <remarks>
/// The governing asymmetry, and the reason nearly every test here is about a <i>negative</i> result: a
/// mention in a label is real evidence, and a silence is not. openFDA publishes no structured interaction
/// data — only prose written per product — so "neither label names the other" cannot be reported as an
/// all-clear without inventing an assurance no source gave.
/// </remarks>
public class LabelInteractionCheckTests
{
    private const string WarfarinText =
        "Concomitant use of amiodarone increases the INR and the risk of bleeding.";

    [Fact]
    public void Warns_when_one_drugs_label_names_another_on_the_same_prescription()
    {
        var warfarin = Fx.Line(name: "Marevan 5mg");
        var amiodarone = Fx.Line(name: "Cordarone 200mg");

        var result = Fx.Run(
            Fx.Request([warfarin, amiodarone]),
            Fx.Snapshot(labels: Fx.Labels(
                Fx.Label(warfarin.DrugId, "warfarin", interactions: WarfarinText),
                Fx.Label(amiodarone.DrugId, "amiodarone"))));

        var finding = result.LabelFor(warfarin.LineId, CheckKind.Interaction);
        finding.State.Should().Be(CheckState.Warning);
        finding.RelatedLineId.Should().Be(amiodarone.LineId);
        finding.ReferenceText.Should().Contain("increases the INR");
        // A clinical check may never block, whatever it found — the type system already forbids it, and this
        // pins the behaviour a prescriber depends on: the line stays submittable with a recorded reason.
        finding.IsBlocking.Should().BeFalse();
        finding.RequiresAcknowledgement.Should().BeTrue();
    }

    [Fact]
    public void Warns_when_the_OTHER_drugs_label_is_the_one_carrying_the_warning()
    {
        // Manufacturers do not document interactions symmetrically: often only the older drug's label
        // mentions the newer one. A single-direction scan silently misses half of everything findable.
        var quiet = Fx.Line(name: "Quiet drug");
        var talkative = Fx.Line(name: "Talkative drug");

        var result = Fx.Run(
            Fx.Request([quiet, talkative]),
            Fx.Snapshot(labels: Fx.Labels(
                Fx.Label(quiet.DrugId, "quietine", interactions: "No known interactions."),
                Fx.Label(talkative.DrugId, "talkatinib",
                    interactions: "Avoid co-administration with quietine; plasma levels rise."))));

        result.LabelFor(quiet.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Warning);
    }

    [Fact]
    public void Assigns_no_severity_because_the_label_states_an_effect_not_a_grade()
    {
        var a = Fx.Line();
        var b = Fx.Line(name: "Amiodarone");

        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(labels: Fx.Labels(
                Fx.Label(a.DrugId, "warfarin", interactions: WarfarinText),
                Fx.Label(b.DrugId, "amiodarone"))));

        // Inventing "Major" here would put a clinical grade on the platform's letterhead that no source
        // supplied, and prescribers weigh severity heavily.
        result.LabelFor(a.LineId, CheckKind.Interaction).Severity.Should().BeNull();
    }

    [Fact]
    public void A_clean_scan_is_NOT_checked_rather_than_ok()
    {
        var a = Fx.Line();
        var b = Fx.Line();

        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(labels: Fx.Labels(
                Fx.Label(a.DrugId, "drug a", interactions: "No relevant interactions are known."),
                Fx.Label(b.DrugId, "drug b", interactions: "Nothing of note."))));

        var finding = result.LabelFor(a.LineId, CheckKind.Interaction);

        // The whole point. A label's interactions section is a narrative, not a complete list, so an
        // interaction can exist without being named — and a green tick here would be an assurance no source
        // ever gave.
        finding.State.Should().Be(CheckState.NotChecked);
        finding.State.Should().NotBe(CheckState.Ok);
        finding.MessageEn.Should().Contain("not an all-clear");
    }

    [Fact]
    public void Says_so_when_a_label_could_not_be_matched()
    {
        var known = Fx.Line();
        var obscure = Fx.Line(name: "Local herbal tonic");

        var result = Fx.Run(
            Fx.Request([known, obscure]),
            Fx.Snapshot(labels: Fx.LabelsUnmatched(
                obscure.DrugId, "no U.S. label is published under \"herbal tonic\"",
                Fx.Label(known.DrugId, "drug a", interactions: "Nothing of note."))));

        // Naming which drug went unchecked is what lets a prescriber decide whether it matters. "Not
        // checked" without the reason is indistinguishable from the system not bothering.
        result.LabelFor(known.LineId, CheckKind.Interaction).MessageEn
            .Should().Contain("Local herbal tonic");
        result.LabelFor(obscure.LineId, CheckKind.Interaction).MessageEn
            .Should().Contain("no U.S. label is published");
    }

    [Fact]
    public void Still_warns_from_the_one_label_it_did_get()
    {
        // Half the evidence is not none of it: if A's own label names B by name, that warning stands whether
        // or not B's label could be fetched.
        var known = Fx.Line(name: "Warfarin");
        var missing = Fx.Line(name: "amiodarone");

        var result = Fx.Run(
            Fx.Request([known, missing]),
            Fx.Snapshot(labels: Fx.LabelsUnmatched(
                missing.DrugId, "no label",
                Fx.Label(known.DrugId, "warfarin", interactions: WarfarinText))));

        result.LabelFor(known.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Warning);
    }

    [Fact]
    public void A_failed_lookup_is_unavailable_not_not_checked()
    {
        var line = Fx.Line();
        var other = Fx.Line();

        var result = Fx.Run(
            Fx.Request([line, other]),
            Fx.Snapshot(labels: Fx.LabelsFailed(line.DrugId, "the label service is rate-limited right now")));

        var finding = result.LabelFor(line.LineId, CheckKind.Interaction);

        // "There is no such label" is an answer; "we could not find out" is not. Collapsing an outage into
        // the quiet NotChecked a genuinely unlisted product produces is exactly what Fetched<T> exists to
        // stop, and Unavailable outranks Warning in the roll-up so it cannot be hidden by another finding.
        finding.State.Should().Be(CheckState.Unavailable);
        finding.MessageEn.Should().Contain("rate-limited");
    }

    [Fact]
    public void The_whole_source_being_down_leaves_every_line_unavailable()
    {
        var a = Fx.Line();
        var b = Fx.Line();

        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(labels: Fetched.NotAvailable<LabelEvidence>("openFDA did not respond in time")));

        result.LabelFor(a.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Unavailable);
        result.LabelFor(b.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Unavailable);
    }

    [Fact]
    public void Says_nothing_at_all_when_there_is_no_second_drug_to_pair_with()
    {
        var only = Fx.Line();

        var result = Fx.Run(
            Fx.Request([only]),
            Fx.Snapshot(labels: Fx.Labels(Fx.Label(only.DrugId, "drug a", interactions: WarfarinText))));

        // Inapplicable, not skipped. Because this pass can never return Ok, anything it emits stops its line
        // from ever summarising as Ok — so a permanent "not checked" on every single-drug prescription would
        // leave them all in the unchecked state for ever and drain the meaning out of that state on the
        // prescriptions where something really was skipped.
        result.Findings.Where(f => f.Kind == CheckKind.Interaction)
            .Should().ContainSingle("only the curated check should speak when there is no pair");
    }

    [Fact]
    public void A_clean_multi_drug_line_cannot_summarise_as_Ok()
    {
        // The cost of "never green", stated once so it is a decision rather than a surprise: as soon as a
        // prescription has two drugs on it, the label pass contributes a NotChecked that outranks Ok in the
        // roll-up, and the line reads "not fully checked" however clean everything else is.
        var a = Fx.Line();
        var b = Fx.Line();

        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(
                indications: Fx.Indications(
                    new DrugIndicationFact(a.DrugId, ["E11"]), new DrugIndicationFact(b.DrugId, ["E11"])),
                interactions: Fx.Interactions(knownPairCount: 40),
                allergies: Fx.Allergies(recordedCount: 2),
                labels: Fx.Labels(
                    Fx.Label(a.DrugId, "drug a", interactions: "Nothing of note."),
                    Fx.Label(b.DrugId, "drug b", interactions: "Nothing of note."))), diagnoses: ["E11.9"]);

        result.StateFor(a.LineId).Should().Be(CheckState.NotChecked);
        result.UnacknowledgedBlockers(a.LineId).Should().BeEmpty("nothing here needs a reason to proceed");
    }

    [Fact]
    public void Does_not_check_a_drug_against_itself()
    {
        // The same molecule twice — two strengths of one product — is a duplication problem, not an
        // interaction, and a label naming its own ingredient would otherwise warn against itself.
        var drugId = Guid.NewGuid();
        var morning = Fx.Line(drugId: drugId, name: "Warfarin 3mg");
        var evening = Fx.Line(drugId: drugId, name: "Warfarin 5mg");

        var result = Fx.Run(
            Fx.Request([morning, evening]),
            Fx.Snapshot(labels: Fx.Labels(Fx.Label(drugId, "warfarin", interactions: "Contains warfarin."))));

        result.Findings.Where(f => f.LineId == morning.LineId && f.Kind == CheckKind.Interaction)
            .Should().ContainSingle("the same molecule twice is a duplication question, not an interaction");
    }

    [Fact]
    public void Runs_alongside_the_curated_list_without_replacing_it()
    {
        var a = Fx.Line();
        var b = Fx.Line(name: "Amiodarone");

        var result = Fx.Run(
            Fx.Request([a, b]),
            Fx.Snapshot(
                interactions: Fx.Interactions(knownPairCount: 137),
                labels: Fx.Labels(
                    Fx.Label(a.DrugId, "warfarin", interactions: WarfarinText),
                    Fx.Label(b.DrugId, "amiodarone"))));

        // Two sources with different authority and different provenance. Collapsing them into one verdict
        // would hide which one spoke — and the curated list is the one a pharmacist can correct.
        result.For(a.LineId, CheckKind.Interaction).Provenance!.SourceName.Should().Be(Fx.Provenance.SourceName);
        result.LabelFor(a.LineId, CheckKind.Interaction).Provenance!.SourceName
            .Should().Be(Fx.LabelProvenance.SourceName);
        result.StateFor(a.LineId).Should().Be(CheckState.Warning);
    }
}

/// <summary>Dose and duration against the manufacturer's labelled dosing.</summary>
public class LabelDoseReferenceTests
{
    private const string DosingText = "2.1 Individualized Dosing The dosage must be individualized "
        + "according to the patient's INR response.";

    [Fact]
    public void Shows_the_labelled_dosing_but_does_not_grade_it()
    {
        var line = Fx.Line(doseAmount: 10, doseUnit: "mg", timesPerDay: 3, durationDays: 30);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(labels: Fx.Labels(
                Fx.Label(line.DrugId, "warfarin", dosing: DosingText, strengths: "1mg, 2mg, 5mg tablets"))));

        var finding = result.For(line.LineId, CheckKind.DoseDuration);

        // NotChecked, with the text. openFDA publishes dosing as prose and no structured maximum daily dose
        // or treatment length exists anywhere in the dataset; extracting a ceiling from that would produce a
        // number the platform cannot defend, and would fail hardest on the narrative-dosed drugs where a
        // wrong ceiling does the most harm.
        finding.State.Should().Be(CheckState.NotChecked);
        finding.MessageEn.Should().Contain("NOT been compared");
        finding.ReferenceText.Should().Contain("individualized").And.Contain("5mg tablets");
    }

    [Fact]
    public void A_configured_rule_still_decides_and_the_label_does_not_override_it()
    {
        var line = Fx.Line(doseAmount: 1000, doseUnit: "mg", timesPerDay: 4);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                dosingRules: Fx.DosingRules(new DosingRuleFact(line.DrugId, MaxDailyDose: 3000, DoseUnit: "mg")),
                labels: Fx.Labels(Fx.Label(line.DrugId, "paracetamol", dosing: DosingText))));

        // The curated rule is the authored, defensible source. Label prose is reference material and must
        // not dilute a real ceiling a pharmacist wrote.
        result.For(line.LineId, CheckKind.DoseDuration).State.Should().Be(CheckState.Warning);
    }

    [Fact]
    public void Falls_back_to_the_plain_no_rule_answer_when_no_label_was_retrieved()
    {
        var line = Fx.Line();

        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(labels: Fx.Labels()));

        var finding = result.For(line.LineId, CheckKind.DoseDuration);
        finding.State.Should().Be(CheckState.NotChecked);
        finding.ReferenceText.Should().BeNull();
    }
}
