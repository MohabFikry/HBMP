using FluentAssertions;
using Mersal.Approvals.Domain;

namespace Mersal.Approvals.Tests;

/// <summary>Phase 7.2 pure decision guards (23-state-machines §5): mandatory-rationale blankness, the partial
/// approval scope check (non-empty strict subset), and the TAT / SLA-breach computation. No DB.</summary>
public class DecisionRulesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("out of policy", false)]
    public void Blank_detects_missing_rationale(string? value, bool blank) =>
        DecisionRules.IsBlank(value).Should().Be(blank);

    [Fact]
    public void Partial_scope_must_be_a_non_empty_strict_subset()
    {
        string[] requested = ["70450", "80053", "85025"];

        DecisionRules.ValidatePartialScope(requested, []).Should().Be(PartialScopeError.Empty);
        DecisionRules.ValidatePartialScope(requested, ["70450", "99999"]).Should().Be(PartialScopeError.NotSubset);
        DecisionRules.ValidatePartialScope(requested, ["70450", "80053", "85025"]).Should().Be(PartialScopeError.EqualsFull);
        DecisionRules.ValidatePartialScope(requested, ["70450", "80053"]).Should().Be(PartialScopeError.None);
        // order-insensitive + de-duplicated
        DecisionRules.ValidatePartialScope(requested, ["80053", "70450", "70450"]).Should().Be(PartialScopeError.None);
    }

    [Fact]
    public void Tat_is_whole_seconds_and_never_negative()
    {
        var submitted = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);
        DecisionRules.TatSeconds(submitted, submitted.AddMinutes(30)).Should().Be(1800);
        DecisionRules.TatSeconds(submitted, submitted.AddSeconds(-5)).Should().Be(0);
    }

    [Fact]
    public void Sla_breach_is_flagged_only_past_the_due_instant()
    {
        var due = new DateTimeOffset(2026, 7, 25, 13, 0, 0, TimeSpan.Zero);
        DecisionRules.SlaBreached(due, due.AddMinutes(1)).Should().BeTrue();
        DecisionRules.SlaBreached(due, due.AddMinutes(-1)).Should().BeFalse();
        DecisionRules.SlaBreached(null, due).Should().BeFalse();   // no timer set → no breach
    }
}
