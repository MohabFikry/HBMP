using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Mersal.Authz.Tests;

/// <summary>
/// 21.4 — the third, orthogonal gate (design 40 §4, adaptation A4).
///
/// The property under test is that a programme refusal is DISTINGUISHABLE from a permission refusal. Both
/// are 403s, and it would be easy to ship them as the same shape — but the remedies differ ("ask your
/// administrator" vs "ask Mersal"), so a client that cannot tell them apart sends every user to the wrong
/// place. Under A4 that matters doubly: these are partner NGOs, and the copy must never read as a paywall.
/// </summary>
public class ProgramEnablementTests
{
    private static ProblemDetails Problem(IResult result)
    {
        result.Should().BeAssignableTo<ProblemHttpResult>();
        return ((ProblemHttpResult)result).ProblemDetails;
    }

    // ---- features ---------------------------------------------------------------------------------------

    [Fact]
    public void An_unconfigured_feature_is_disabled()
    {
        // Fail closed. Defaulting the other way would enable every module for every tenant the moment the
        // table shipped empty — which is exactly the state right after this migration deploys.
        ProgramEnablement.IsEnabled(new Dictionary<string, bool>(), ProgramFeatures.Claims).Should().BeFalse();
    }

    [Fact]
    public void An_explicitly_disabled_feature_is_disabled()
    {
        ProgramEnablement.IsEnabled(
            new Dictionary<string, bool> { [ProgramFeatures.Claims] = false }, ProgramFeatures.Claims)
            .Should().BeFalse();
    }

    [Fact]
    public void An_enabled_feature_is_enabled()
    {
        ProgramEnablement.IsEnabled(
            new Dictionary<string, bool> { [ProgramFeatures.Claims] = true }, ProgramFeatures.Claims)
            .Should().BeTrue();
    }

    // ---- the distinction that matters -------------------------------------------------------------------

    [Fact]
    public void THE_acceptance_case_a_programme_refusal_is_not_a_permission_refusal()
    {
        var notEnabled = Problem(ProgramEnablement.NotEnabled(ProgramFeatures.Claims));
        // A real permission denial, in the shape the services actually emit.
        var forbidden = Problem(GateResults.Forbidden(
            "urn:hbmp:claims-access-denied", detail: "You are not permitted to perform this claims action."));

        notEnabled.Status.Should().Be(403);
        forbidden.Status.Should().Be(403);

        // Same status, different TYPE — which is the only thing a client can branch on reliably.
        notEnabled.Type.Should().Be(ProgramEnablement.NotEnabledType);
        notEnabled.Type.Should().NotBe(forbidden.Type,
            "the SPA must be able to show the not-enabled treatment rather than the permission-denied one");
        notEnabled.Extensions["code"].Should().Be(ProgramEnablement.NotEnabledCode);
    }

    [Fact]
    public void A_programme_refusal_names_the_programme()
    {
        // A generic wall produces a support ticket that has to be answered with a question. Naming the
        // feature lets the SPA say which module, and lets support act on the first message.
        Problem(ProgramEnablement.NotEnabled(ProgramFeatures.Interop))
            .Extensions["feature"].Should().Be(ProgramFeatures.Interop);
    }

    [Fact]
    public void A_programme_refusal_does_not_read_as_a_paywall()
    {
        // A4: Mersal is a charity and these are partner NGOs, not customers on a price plan. Wording is
        // part of the contract here, so it is asserted rather than left to whoever edits the string next.
        var detail = Problem(ProgramEnablement.NotEnabled(ProgramFeatures.Claims)).Detail!;

        foreach (var word in new[] { "upgrade", "plan", "subscription", "billing", "purchase", "trial", "pay" })
            detail.Should().NotContainEquivalentOf(word, "A4 — enablement is onboarding, not commercial upsell");

        detail.Should().Contain("Mersal", "the remedy is to contact Mersal, and the message must say so");
    }

    [Fact]
    public void A_limit_refusal_is_its_own_type_and_reports_the_numbers()
    {
        var p = Problem(ProgramEnablement.LimitReached(ProgramLimits.ActiveUsers, max: 25, current: 25));

        p.Status.Should().Be(403);
        p.Type.Should().Be(ProgramEnablement.LimitReachedType);
        p.Type.Should().NotBe(ProgramEnablement.NotEnabledType,
            "'not on the programme' and 'at your limit' have different remedies too");

        // "You are at your limit" without the numbers is not actionable — an administrator cannot tell
        // whether to free a slot or ask for a larger cap.
        p.Extensions["max"].Should().Be(25);
        p.Extensions["current"].Should().Be(25);
        p.Extensions["limit"].Should().Be(ProgramLimits.ActiveUsers);
    }

    [Fact]
    public void Problem_documents_serialize_with_their_type_intact()
    {
        // The type only helps if it survives serialization — this is the field the SPA branches on.
        var json = JsonSerializer.Serialize(Problem(ProgramEnablement.NotEnabled(ProgramFeatures.Claims)));
        json.Should().Contain("program-not-enabled");
    }

    // ---- caps -------------------------------------------------------------------------------------------

    [Fact]
    public void No_configured_cap_means_unlimited()
    {
        // Fail OPEN here, deliberately, and in the opposite direction to features: inventing a default cap
        // would take a working platform offline the day this shipped, for tenants nobody had configured.
        ProgramEnablement.WouldBreach(max: null, liveCount: 10_000).Should().BeFalse();
    }

    [Theory]
    [InlineData(5, 4, false)]  // room for one more
    [InlineData(5, 5, true)]   // at the cap — the next one breaches
    [InlineData(5, 6, true)]   // already over (a cap lowered below the current count)
    [InlineData(0, 0, true)]   // a cap of zero permits nothing
    public void A_cap_of_N_permits_exactly_N_rows(int max, int live, bool breaches)
    {
        // The boundary is where this goes wrong: off by one either lets a tenant exceed the cap by one, or
        // refuses the Nth user of an N-user allocation, which reads as the cap being one smaller than agreed.
        ProgramEnablement.WouldBreach(max, live).Should().Be(breaches);
    }

    [Fact]
    public void Freeing_a_row_frees_the_slot_immediately()
    {
        // Live counting makes this true by construction. With a stored counter it would be true only if
        // every deletion path remembered to decrement — and the one that forgot would be undetectable.
        ProgramEnablement.WouldBreach(max: 5, liveCount: 5).Should().BeTrue();
        ProgramEnablement.WouldBreach(max: 5, liveCount: 4).Should().BeFalse();
    }
}
