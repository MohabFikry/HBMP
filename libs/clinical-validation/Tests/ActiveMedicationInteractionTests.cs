using FluentAssertions;

namespace Mersal.ClinicalValidation.Tests;

/// <summary>
/// 32.1 — the interaction check against what the beneficiary is ALREADY taking.
/// </summary>
/// <remarks>
/// <para>
/// The engine has had this loop since phase 28. It ran zero times in production for the whole of that
/// period, because <c>ActiveMedicationDrugIds</c> was a plain list on <see cref="ValidationRequest"/> and
/// both call sites passed <c>[]</c> — and every unflagged line was then reported <b>Ok</b>, "no interaction
/// found", about a comparison that had not happened.
/// </para>
/// <para>
/// The fix is the one 28.2 already made for diagnoses: fetched data belongs on the SNAPSHOT behind
/// <see cref="Fetched{T}"/>, where "we could not ask" and "we asked and there is nothing" are different
/// values and neither is an empty list. These tests are what stops it regressing to a list.
/// </para>
/// </remarks>
public class ActiveMedicationInteractionTests
{
    [Fact]
    public void An_interaction_with_a_current_medication_is_reported()
    {
        var warfarin = Guid.NewGuid();
        var aspirin = Fx.Line(name: "Aspirin");

        var result = Fx.Run(
            Fx.Request([aspirin]),
            Fx.Snapshot(
                interactions: Fx.Interactions(100,
                    new InteractionFact(aspirin.DrugId, warfarin, ClinicalSeverity.Major, "Additive bleeding risk")),
                activeMedications: Fx.ActiveMedications(
                    new ActiveMedication(warfarin, "Warfarin", "Prescribed"))));

        var finding = result.For(aspirin.LineId, CheckKind.Interaction);
        finding.State.Should().Be(CheckState.Warning);
        finding.Severity.Should().Be(ClinicalSeverity.Major);
        finding.MessageEn.Should().Contain("Warfarin");
        finding.MessageEn.Should().Contain("already taking");

        // The interaction is with a medicine that is not on this prescription, so there is no sibling line
        // to point at. Reporting one would send the prescriber looking for a line that does not exist.
        finding.RelatedLineId.Should().BeNull();
    }

    [Fact]
    public void The_source_of_a_current_medication_is_named_in_the_warning()
    {
        // A dispensing record and a patient's recollection are both worth acting on and are not equally
        // certain. A prescriber weighing an interaction is entitled to know which one it rests on.
        var stJohnsWort = Guid.NewGuid();
        var line = Fx.Line(name: "Sertraline");

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                interactions: Fx.Interactions(100,
                    new InteractionFact(line.DrugId, stJohnsWort, ClinicalSeverity.Major, "Serotonin syndrome risk")),
                activeMedications: Fx.ActiveMedications(
                    new ActiveMedication(stJohnsWort, "St John's Wort", "SelfReported"))));

        result.For(line.LineId, CheckKind.Interaction).MessageEn.Should().Contain("SelfReported");
    }

    [Fact]
    public void An_unavailable_source_is_never_reported_as_clear()
    {
        var line = Fx.Line(name: "Aspirin");

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(activeMedications: Fetched.NotAvailable<ActiveMedications>("pharmacy unreachable")));

        var finding = result.For(line.LineId, CheckKind.Interaction);
        finding.State.Should().Be(CheckState.Unavailable);
        finding.MessageEn.Should().NotContain("No interaction found");
        finding.MessageEn.Should().Contain("pharmacy unreachable");
    }

    [Fact]
    public void Nothing_recorded_says_so_rather_than_claiming_a_comparison()
    {
        var line = Fx.Line(name: "Aspirin");

        var result = Fx.Run(Fx.Request([line]), Fx.Snapshot(activeMedications: Fx.ActiveMedications()));

        var finding = result.For(line.LineId, CheckKind.Interaction);
        finding.State.Should().Be(CheckState.Ok);
        finding.MessageEn.Should().Contain("no current medications recorded");
    }

    [Fact]
    public void A_clear_result_states_how_many_medications_it_compared_against()
    {
        var line = Fx.Line(name: "Aspirin");

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(activeMedications: Fx.ActiveMedications(
                new ActiveMedication(Guid.NewGuid(), "Metformin", "Prescribed"),
                new ActiveMedication(Guid.NewGuid(), "Amlodipine", "Prescribed"))));

        var finding = result.For(line.LineId, CheckKind.Interaction);
        finding.State.Should().Be(CheckState.Ok);
        finding.MessageEn.Should().Contain("2 current medication");
    }

    [Fact]
    public void A_medicine_already_taken_and_prescribed_again_is_not_an_interaction_with_itself()
    {
        // Re-prescribing a continuing medicine is the ordinary case, not a finding. Duplicate therapy is a
        // different check with a different message.
        var line = Fx.Line(name: "Metformin");

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(
                interactions: Fx.Interactions(100,
                    new InteractionFact(line.DrugId, line.DrugId, ClinicalSeverity.Major, "self")),
                activeMedications: Fx.ActiveMedications(
                    new ActiveMedication(line.DrugId, "Metformin", "Prescribed"))));

        result.For(line.LineId, CheckKind.Interaction).State.Should().Be(CheckState.Ok);
    }
}
