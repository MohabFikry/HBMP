using FluentAssertions;
using Mersal.Eligibility.Domain;

namespace Mersal.Eligibility.Tests;

public class EligibilityEngineTests
{
    private static readonly DateOnly Today = new(2026, 7, 22);

    private static CoverageView Coverage(
        string category = "CONSULT", bool active = true,
        decimal limit = 10, decimal consumed = 0, LimitType type = LimitType.Count,
        DateOnly? from = null, DateOnly? to = null)
        => new(Guid.NewGuid(), category, active,
            from ?? Today.AddMonths(-1), to ?? Today.AddMonths(6),
            [new LimitState(type, limit, consumed)]);

    private static EligibilityRequest Req(
        MemberStatus status = MemberStatus.Active, string category = "CONSULT",
        bool preAuth = false, params CoverageView[] coverages)
        => new(status, category, ServiceCode: "C001", preAuth, coverages, Today);

    [Fact]
    public void Active_with_valid_coverage_and_remaining_is_Eligible()
    {
        var r = EligibilityEngine.Evaluate(Req(coverages: Coverage(limit: 10, consumed: 3)));
        r.Decision.Should().Be(EligibilityDecision.Eligible);
        r.CoverageId.Should().NotBeNull();
        r.LimitState!.Remaining.Should().Be(7);
    }

    [Theory]
    [InlineData(MemberStatus.Suspended)]
    [InlineData(MemberStatus.Expired)]
    [InlineData(MemberStatus.Blocked)]
    [InlineData(MemberStatus.Inactive)]
    [InlineData(MemberStatus.Pending)]
    public void Non_active_member_is_Ineligible_with_reason(MemberStatus status)
    {
        var r = EligibilityEngine.Evaluate(Req(status: status, coverages: Coverage()));
        r.Decision.Should().Be(EligibilityDecision.Ineligible);
        r.Reasons.Should().ContainSingle().Which.Should().Contain(status.ToString());
        r.CoverageId.Should().BeNull();
    }

    [Fact]
    public void No_coverage_for_category_is_Ineligible()
    {
        var r = EligibilityEngine.Evaluate(Req(category: "PHARMACY", coverages: Coverage(category: "CONSULT")));
        r.Decision.Should().Be(EligibilityDecision.Ineligible);
        r.Reasons.Should().ContainSingle().Which.Should().Contain("no active coverage");
    }

    [Fact]
    public void Inactive_coverage_is_Ineligible()
    {
        var r = EligibilityEngine.Evaluate(Req(coverages: Coverage(active: false)));
        r.Decision.Should().Be(EligibilityDecision.Ineligible);
    }

    [Fact]
    public void Coverage_out_of_effective_window_is_Ineligible()
    {
        var expired = Coverage(from: Today.AddYears(-2), to: Today.AddYears(-1));
        var r = EligibilityEngine.Evaluate(Req(coverages: expired));
        r.Decision.Should().Be(EligibilityDecision.Ineligible);
    }

    [Fact]
    public void Exhausted_limit_is_NeedsAuthorization_not_a_hard_No()
    {
        var r = EligibilityEngine.Evaluate(Req(coverages: Coverage(limit: 5, consumed: 5)));
        r.Decision.Should().Be(EligibilityDecision.NeedsAuthorization);
        r.Reasons.Should().Contain(x => x.Contains("limit reached"));
        r.CoverageId.Should().NotBeNull();
    }

    [Fact]
    public void Gated_service_requiring_preauth_is_NeedsAuthorization()
    {
        var r = EligibilityEngine.Evaluate(Req(preAuth: true, coverages: Coverage(limit: 10, consumed: 0)));
        r.Decision.Should().Be(EligibilityDecision.NeedsAuthorization);
        r.Reasons.Should().Contain(x => x.Contains("pre-authorization"));
    }

    [Fact]
    public void Binding_limit_is_the_least_remaining_across_multiple_limits()
    {
        var coverage = new CoverageView(Guid.NewGuid(), "CONSULT", true,
            Today.AddMonths(-1), Today.AddMonths(6),
            [new LimitState(LimitType.Annual, 1000, 100), new LimitState(LimitType.Count, 12, 11)]);
        var r = EligibilityEngine.Evaluate(Req(coverages: coverage));
        r.Decision.Should().Be(EligibilityDecision.Eligible);
        r.LimitState!.LimitType.Should().Be(LimitType.Count);
        r.LimitState.Remaining.Should().Be(1);
    }

    [Fact]
    public void Decision_domain_is_exactly_three_values()
        => Enum.GetValues<EligibilityDecision>().Should().BeEquivalentTo(
            new[] { EligibilityDecision.Eligible, EligibilityDecision.Ineligible, EligibilityDecision.NeedsAuthorization });
}
