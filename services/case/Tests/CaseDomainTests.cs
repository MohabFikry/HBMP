using FluentAssertions;
using Mersal.Case.Domain;

namespace Mersal.Case.Tests;

/// <summary>Pure domain rules: the case / task / escalation state machines and the field-scoped 360 building
/// blocks (masked sections). No datastore.</summary>
public class CaseDomainTests
{
    [Theory]
    [InlineData(CaseStatus.Open, CaseStatus.Active, true)]
    [InlineData(CaseStatus.Active, CaseStatus.OnHold, true)]
    [InlineData(CaseStatus.OnHold, CaseStatus.Active, true)]
    [InlineData(CaseStatus.Active, CaseStatus.Resolved, true)]
    [InlineData(CaseStatus.Resolved, CaseStatus.Closed, true)]
    [InlineData(CaseStatus.Closed, CaseStatus.Active, false)]   // terminal
    [InlineData(CaseStatus.Open, CaseStatus.Resolved, false)]   // must be Active first
    public void Case_transitions_follow_the_state_machine(CaseStatus from, CaseStatus to, bool legal) =>
        CaseWorkflow.CanTransition(from, to).Should().Be(legal);

    [Theory]
    [InlineData(TaskState.Todo, TaskState.InProgress, true)]
    [InlineData(TaskState.InProgress, TaskState.Done, true)]
    [InlineData(TaskState.Done, TaskState.Todo, false)]         // terminal
    [InlineData(TaskState.Cancelled, TaskState.InProgress, false)]
    public void Task_transitions_follow_the_state_machine(TaskState from, TaskState to, bool legal) =>
        CaseWorkflow.CanTransition(from, to).Should().Be(legal);

    [Theory]
    [InlineData(EscalationStatus.Raised, EscalationStatus.Acknowledged, true)]
    [InlineData(EscalationStatus.Raised, EscalationStatus.Resolved, true)]
    [InlineData(EscalationStatus.Acknowledged, EscalationStatus.Resolved, true)]
    [InlineData(EscalationStatus.Resolved, EscalationStatus.Raised, false)]
    public void Escalation_transitions_follow_the_state_machine(EscalationStatus from, EscalationStatus to, bool legal) =>
        CaseWorkflow.CanTransition(from, to).Should().Be(legal);

    [Fact]
    public void Case_number_is_year_scoped_and_zero_padded()
    {
        CaseNo.Format(2026, 42).Should().Be("CASE-2026-000042");
        string.CompareOrdinal(CaseNo.Format(2026, 2), CaseNo.Format(2026, 1)).Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_masked_clinical_section_reports_presence_but_is_always_summary_only()
    {
        var m = MaskedSection.Of(3);
        m.Count.Should().Be(3);
        m.SummaryOnly.Should().BeTrue();   // never the record body — only that N exist
        MaskedSection.None.Count.Should().Be(0);
    }

    [Fact]
    public void The_360_field_classes_expose_a_coordination_summary_never_raw_clinical_bodies()
    {
        var raw = new[] { "emr_note", "prescription", "lab_result", "imaging_result" };
        Beneficiary360.FieldClasses.Should().Contain("diagnosis_summary");
        Beneficiary360.FieldClasses.Should().NotIntersectWith(raw);
    }
}
