using FluentAssertions;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Tests;

/// <summary>
/// The advisory checks on a composed investigation order.
/// </summary>
/// <remarks>
/// The point of these is the DIVISION, not the coverage: a benefit or fulfilment fact blocks, a clinical
/// observation only warns, and a question nobody can answer says so instead of passing. The same rule the
/// prescribing engine follows, applied to a different set of questions.
/// </remarks>
public class InvestigationChecksTests
{
    private static readonly Guid L1 = Guid.Parse("11111111-0000-0000-0000-000000000001");

    /// <param name="catalogueDown">
    /// True = master data could not be asked, which is a NULL known-set and not an empty one. Expressed as a
    /// flag rather than by passing null, because `known ?? default` silently swallowed the null and the test
    /// for the outage case passed against the happy path.
    /// </param>
    private static InvestigationSnapshot Snapshot(
        bool catalogueDown = false, IEnumerable<string>? open = null,
        IEnumerable<string>? gated = null, int diagnoses = 1) =>
        new(catalogueDown ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "85025", "71046" },
            new HashSet<string>(open ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(gated ?? [], StringComparer.OrdinalIgnoreCase),
            diagnoses);

    private static List<InvestigationLineInput> One(string code) => [new(L1, code, "test", 1)];

    [Fact]
    public void A_code_the_catalogue_does_not_hold_blocks()
    {
        var f = InvestigationChecks.Evaluate(OrderType.Lab, One("99999"), Snapshot());

        // Blocking rather than a warning: no reason a clinician could type makes a code fulfillable that no
        // provider recognises. Submission refuses it too — this only saves them the round trip.
        f.Should().ContainSingle(x => x.Kind == InvestigationCheckKind.Code && x.IsBlocking);
    }

    [Fact]
    public void A_catalogue_that_could_not_be_reached_is_never_reported_as_a_pass()
    {
        var f = InvestigationChecks.Evaluate(OrderType.Lab, One("85025"), Snapshot(catalogueDown: true));

        // The single most important assertion here. "We asked and it is fine" and "we could not ask" are
        // different answers, and only one of them is Ok. Collapsing them is how an outage reads as approval.
        var code = f.Single(x => x.Kind == InvestigationCheckKind.Code);
        code.State.Should().Be(InvestigationCheckState.Unavailable);
        code.State.Should().NotBe(InvestigationCheckState.Ok);
        InvestigationChecks.StateOf(f).Should().NotBe(InvestigationCheckState.Ok);
    }

    [Theory]
    [InlineData(OrderType.Lab, "71046")]      // a chest x-ray on a lab order
    [InlineData(OrderType.Imaging, "85025")]  // a blood count on an imaging order
    public void A_procedure_from_the_wrong_section_blocks(OrderType type, string code)
    {
        var f = InvestigationChecks.Evaluate(type, One(code), Snapshot());

        // Not a judgement call: the order reaches ONE queue, and nobody in a haematology worklist can
        // perform a chest x-ray. There is nothing for a reason to override.
        f.Should().Contain(x => x.Kind == InvestigationCheckKind.Section && x.IsBlocking);
    }

    [Fact]
    public void A_test_already_outstanding_only_warns()
    {
        var f = InvestigationChecks.Evaluate(OrderType.Lab, One("85025"), Snapshot(open: ["85025"]));

        var dup = f.Single(x => x.Kind == InvestigationCheckKind.Duplicate);
        // A doctor may know perfectly well the first one is outstanding and want it repeated. Clinical
        // observations warn; they do not refuse.
        dup.State.Should().Be(InvestigationCheckState.Warning);
        dup.RequiresAcknowledgement.Should().BeTrue();
        dup.IsBlocking.Should().BeFalse();
    }

    [Fact]
    public void Needing_pre_authorization_is_stated_but_is_not_a_warning()
    {
        var f = InvestigationChecks.Evaluate(OrderType.Imaging, One("71046"), Snapshot(gated: ["71046"]));

        var auth = f.Single(x => x.Kind == InvestigationCheckKind.PriorAuthorization);
        // Routing through the benefit scheme correctly is not a deviation. Making the clinician type a
        // reason to proceed past it would teach them that the reason box means nothing.
        auth.State.Should().Be(InvestigationCheckState.Ok);
        auth.RequiresAcknowledgement.Should().BeFalse();
        auth.MessageEn.Should().Contain("approval team");
    }

    [Fact]
    public void The_indication_check_admits_it_has_no_reference_rather_than_passing()
    {
        var f = InvestigationChecks.Evaluate(OrderType.Lab, One("85025"), Snapshot());

        var ind = f.Single(x => x.Kind == InvestigationCheckKind.Indication);
        ind.State.Should().Be(InvestigationCheckState.NotChecked);
        ind.Caveat.Should().NotBeNull();
    }

    [Fact]
    public void A_line_whose_only_open_question_is_the_indication_does_not_show_as_a_clean_pass()
    {
        var f = InvestigationChecks.Evaluate(OrderType.Lab, One("85025"), Snapshot());

        // Everything answerable came back fine, and the chip still must not say Ok — because one question
        // was not asked, and "checked and fine" is a different claim from "not looked at".
        InvestigationChecks.StateOf(f).Should().Be(InvestigationCheckState.NotChecked);
    }

    [Fact]
    public void An_empty_line_blocks_rather_than_being_ignored()
    {
        var f = InvestigationChecks.Evaluate(OrderType.Lab, [new(L1, "  ", null, 1)], Snapshot());
        f.Should().ContainSingle(x => x.Kind == InvestigationCheckKind.Code && x.IsBlocking);
    }

    [Fact]
    public void Section_ranges_follow_the_CPT_book()
    {
        InvestigationChecks.IsRadiology("71046").Should().BeTrue();
        InvestigationChecks.IsLaboratory("85025").Should().BeTrue();
        // Category II / III / PLA codes are four digits plus a letter and belong to neither section.
        InvestigationChecks.IsRadiology("0500F").Should().BeFalse();
        InvestigationChecks.IsLaboratory("0016U").Should().BeFalse();
        InvestigationChecks.IsRadiology(null).Should().BeFalse();
    }
}
