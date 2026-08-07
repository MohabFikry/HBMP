using FluentAssertions;
using Mersal.Approvals.Domain;

namespace Mersal.Approvals.Tests;

/// <summary>
/// The conditions an auto-approval must clear (ADR-0035 §5.3).
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in the platform where money is committed with no human in the loop, so the tests
/// here are mostly about what it REFUSES. Every gate fails toward the human: there is no input — not a missing
/// switch, not an unpriced request, not an unreadable rule — that produces an approval by omission.
/// </para>
/// <para>
/// There is no auto-reject and there is no test for one. The two failure modes are not symmetric: a wrong
/// auto-approval costs the payer money and a human reviews the claim later, while a wrong auto-rejection
/// denies care to a refugee with nobody having looked.
/// </para>
/// </remarks>
public class AutoApprovalTests
{
    private static AutoApproveAction Rule(decimal ceiling = 1_000m) =>
        new(ceiling, "Routine consultations under the ceiling are not worth a reviewer's morning.");

    [Fact]
    public void The_switch_being_OFF_refuses_everything_and_is_checked_first()
    {
        // Unconditional, and before anything else. This is the control somebody reaches for at 02:00 because a
        // rule is misbehaving; it must not depend on the request being well-formed, the amount being known, or
        // any rule parsing correctly.
        AutoApproval.Check(switchEnabled: false, Rule(), amount: 1m,
            hasOutstandingClinicalWarning: false, categoryExcluded: false)
            .Should().Be(AutoApproveRefusal.SwitchOff);

        // Even with everything else broken, the switch is still the answer given — so an operator who turned
        // it off is told that, rather than being sent to investigate the request.
        AutoApproval.Check(switchEnabled: false, matched: null, amount: null,
            hasOutstandingClinicalWarning: true, categoryExcluded: true)
            .Should().Be(AutoApproveRefusal.SwitchOff);
    }

    [Fact]
    public void No_matching_rule_is_the_ordinary_case_and_refuses()
    {
        AutoApproval.Check(true, matched: null, amount: 100m, false, false)
            .Should().Be(AutoApproveRefusal.NoRule);
    }

    [Fact]
    public void An_UNPRICED_request_is_never_auto_approved()
    {
        // "We could not work out what this costs" is not a small amount. Approving it would be paying an
        // unknown figure without a human — the exact shape of failure the ceiling exists to prevent.
        AutoApproval.Check(true, Rule(), amount: null, false, false)
            .Should().Be(AutoApproveRefusal.AmountUnknown);
    }

    [Fact]
    public void An_outstanding_clinical_warning_refuses()
    {
        // A clinical check may only ever WARN, never block — that is the platform's standing rule. But an
        // outstanding warning is precisely the thing a human is for, and approving past one without anybody
        // reading it would turn "warn" into "ignored".
        AutoApproval.Check(true, Rule(), amount: 10m, hasOutstandingClinicalWarning: true, categoryExcluded: false)
            .Should().Be(AutoApproveRefusal.ClinicalWarning);
    }

    [Fact]
    public void An_excluded_category_refuses()
    {
        AutoApproval.Check(true, Rule(), amount: 10m, false, categoryExcluded: true)
            .Should().Be(AutoApproveRefusal.CategoryExcluded);
    }

    [Fact]
    public void Over_the_rules_own_ceiling_refuses()
    {
        AutoApproval.Check(true, Rule(ceiling: 500m), amount: 501m, false, false)
            .Should().Be(AutoApproveRefusal.OverRuleCeiling);
        AutoApproval.Check(true, Rule(ceiling: 500m), amount: 500m, false, false)
            .Should().Be(AutoApproveRefusal.None);
    }

    [Fact]
    public void The_tenant_hard_maximum_binds_even_when_a_rule_claims_more()
    {
        // The ceiling on the ceiling. Without it "bounded" would mean "bounded by whatever the last person to
        // edit a rule typed", and one mistyped figure would be the entire control.
        var greedy = Rule(ceiling: 1_000_000m);
        AutoApproval.Check(true, greedy, amount: AutoApproval.HardMaximumEgp + 1m, false, false)
            .Should().Be(AutoApproveRefusal.OverHardMaximum);
        AutoApproval.Check(true, greedy, amount: AutoApproval.HardMaximumEgp, false, false)
            .Should().Be(AutoApproveRefusal.None);
    }

    [Fact]
    public void A_request_that_clears_every_gate_is_approved()
    {
        AutoApproval.Check(true, Rule(ceiling: 1_000m), amount: 250m, false, false)
            .Should().Be(AutoApproveRefusal.None);
    }

    [Fact]
    public void Every_refusal_has_its_own_value_so_a_supervisor_can_act_on_it()
    {
        // "It did not auto-approve" is not an answer anybody can do anything with. A rule that never fires
        // needs to be distinguishable between a switch that is off, an amount over the ceiling and an
        // outstanding warning — three different remedies.
        var refusals = new[]
        {
            AutoApproval.Check(false, Rule(), 1m, false, false),
            AutoApproval.Check(true, null, 1m, false, false),
            AutoApproval.Check(true, Rule(), null, false, false),
            AutoApproval.Check(true, Rule(), 1m, false, true),
            AutoApproval.Check(true, Rule(), 1m, true, false),
            AutoApproval.Check(true, Rule(10m), 11m, false, false),
            AutoApproval.Check(true, Rule(decimal.MaxValue), AutoApproval.HardMaximumEgp + 1m, false, false),
        };
        refusals.Should().OnlyHaveUniqueItems();
        refusals.Should().NotContain(AutoApproveRefusal.None);
    }

    [Fact]
    public void A_machine_decision_is_attributed_to_the_RULE_and_never_to_a_person()
    {
        // The ledger is hash-chained so a decision cannot be quietly reattributed later; writing a human's id
        // on a machine's decision would falsify it at the moment of writing instead. The version is part of
        // the attribution because the rule may have been superseded since — "which rule" is not enough.
        var id = Guid.Parse("2f1c0f6e-7a1b-4c3d-9e5f-0a1b2c3d4e5f");
        var attribution = AutoApproval.Attribution(id, 3);

        attribution.Should().Be("rule:2f1c0f6e-7a1b-4c3d-9e5f-0a1b2c3d4e5f@v3");
        attribution.Should().StartWith("rule:");
    }

    [Fact]
    public void There_is_no_auto_reject_family_at_all()
    {
        // Not "disabled" and not "unimplemented" — absent. A wrong auto-approval costs the payer money and a
        // human reviews the claim later; a wrong auto-rejection denies care to a refugee with nobody having
        // looked, and per libs/benefit-pricing's own header they have "no reviewer in the loop and no recovery
        // path". The throughput is available without the harm: route to a priority queue with a stated reason.
        Enum.GetNames<RuleFamily>().Should().NotContain(n => n.Contains("Reject", StringComparison.OrdinalIgnoreCase));
        Enum.GetNames<RuleFamily>().Should().NotContain(n => n.Contains("Deny", StringComparison.OrdinalIgnoreCase));
        Enum.GetNames<RuleFamily>().Should().BeEquivalentTo(["Routing", "Sla", "Preauth", "AutoApprove"]);
    }

    [Fact]
    public void The_hard_maximum_is_a_real_bound_and_not_effectively_infinite()
    {
        // A "hard maximum" of decimal.MaxValue would satisfy every test above and control nothing.
        AutoApproval.HardMaximumEgp.Should().BePositive();
        AutoApproval.HardMaximumEgp.Should().BeLessThan(1_000_000m);
    }
}
