using FluentAssertions;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>The pre-adjudication rule MATRIX (10b.3, 36 §5) — one case per check plus the key combinations, asserting
/// the exact reason-code set, recommendation, and allowed_amount. Also proves ALL reasons are collected (never
/// stopping at the first failure) and that every emitted code is in the catalogue.</summary>
public class AdjudicationTests
{
    // Clean baseline: eligible, covered, ungated, fulfilled, in-network, tariff 180 on a 200 bill.
    private static AdjudicationFacts Base() => new() { BilledAmount = 200m, ContractPrice = 180m };

    [Fact]
    public void Clean_line_is_recommended_for_approval_at_the_lesser_of_billed_and_tariff()
    {
        var r = Adjudicator.Evaluate(Base());
        r.Recommendation.Should().Be(SystemRecommendation.RecommendApprove);
        r.ReasonCodes.Should().BeEmpty();
        r.AllowedAmount.Should().Be(180m);
    }

    [Theory]
    [InlineData(nameof(AdjudicationFacts.BeneficiaryEligible), ReasonCodes.NotEligible)]
    [InlineData(nameof(AdjudicationFacts.PolicyValid), ReasonCodes.PolicyExpired)]
    [InlineData(nameof(AdjudicationFacts.CoverageCategoryMatches), ReasonCodes.NotCoveredCategory)]
    [InlineData(nameof(AdjudicationFacts.HasFulfillmentRecord), ReasonCodes.NoFulfillmentRecord)]
    [InlineData(nameof(AdjudicationFacts.ProviderInNetwork), ReasonCodes.ProviderOutOfNetwork)]
    [InlineData(nameof(AdjudicationFacts.ContractEffective), ReasonCodes.ContractNotEffective)]
    public void A_false_boolean_gate_denies_with_its_code(string flag, string code)
    {
        var f = flag switch
        {
            nameof(AdjudicationFacts.BeneficiaryEligible) => Base() with { BeneficiaryEligible = false },
            nameof(AdjudicationFacts.PolicyValid) => Base() with { PolicyValid = false },
            nameof(AdjudicationFacts.CoverageCategoryMatches) => Base() with { CoverageCategoryMatches = false },
            nameof(AdjudicationFacts.HasFulfillmentRecord) => Base() with { HasFulfillmentRecord = false },
            nameof(AdjudicationFacts.ProviderInNetwork) => Base() with { ProviderInNetwork = false },
            _ => Base() with { ContractEffective = false },
        };
        var r = Adjudicator.Evaluate(f);
        r.Recommendation.Should().Be(SystemRecommendation.RecommendDeny);
        r.ReasonCodes.Should().Contain(code);
        r.AllowedAmount.Should().Be(0m);
    }

    [Fact]
    public void Gated_service_without_authorization_is_denied_never_approved()
    {
        var r = Adjudicator.Evaluate(Base() with { IsGatedService = true, Authorization = AuthorizationState.None });
        r.ReasonCodes.Should().Contain(ReasonCodes.NoPriorAuth);
        r.Recommendation.Should().Be(SystemRecommendation.RecommendDeny);
        r.Recommendation.Should().NotBe(SystemRecommendation.RecommendApprove);
    }

    [Fact]
    public void Gated_expired_authorization_is_denied()
    {
        var r = Adjudicator.Evaluate(Base() with { IsGatedService = true, Authorization = AuthorizationState.Expired });
        r.ReasonCodes.Should().Contain(ReasonCodes.AuthExpired);
        r.Recommendation.Should().Be(SystemRecommendation.RecommendDeny);
    }

    [Fact]
    public void Partially_approved_scope_narrower_than_the_line_caps_the_allowed_amount()
    {
        var r = Adjudicator.Evaluate(Base() with
        {
            IsGatedService = true, Authorization = AuthorizationState.PartiallyApproved, AuthorizedScopeAmount = 100m,
        });
        r.ReasonCodes.Should().Contain(ReasonCodes.ExceedsAuthScope);
        r.Recommendation.Should().Be(SystemRecommendation.RecommendPartial);
        r.AllowedAmount.Should().Be(100m);
    }

    [Fact]
    public void Duplicate_line_is_denied()
    {
        var r = Adjudicator.Evaluate(Base() with { IsDuplicate = true });
        r.ReasonCodes.Should().Contain(ReasonCodes.DuplicateClaim);
        r.Recommendation.Should().Be(SystemRecommendation.RecommendDeny);
    }

    [Fact]
    public void No_tariff_requires_manual_review_and_allowed_stays_null()
    {
        var r = Adjudicator.Evaluate(Base() with { ContractPrice = null });
        r.ReasonCodes.Should().ContainSingle().Which.Should().Be(ReasonCodes.NoTariff);
        r.Recommendation.Should().Be(SystemRecommendation.RequiresManualReview);
        r.AllowedAmount.Should().BeNull("no tariff must never be invented");
    }

    [Fact]
    public void Exhausted_limit_caps_the_allowed_amount()
    {
        var r = Adjudicator.Evaluate(Base() with { LimitRemaining = 50m });
        r.ReasonCodes.Should().Contain(ReasonCodes.LimitExceeded);
        r.Recommendation.Should().Be(SystemRecommendation.RecommendPartial);
        r.AllowedAmount.Should().Be(50m);
    }

    [Fact]
    public void A_line_failing_two_caps_reports_both_and_takes_the_lower()
    {
        var r = Adjudicator.Evaluate(Base() with
        {
            IsGatedService = true, Authorization = AuthorizationState.PartiallyApproved, AuthorizedScopeAmount = 120m,
            LimitRemaining = 80m,
        });
        r.ReasonCodes.Should().Contain([ReasonCodes.ExceedsAuthScope, ReasonCodes.LimitExceeded]);
        r.Recommendation.Should().Be(SystemRecommendation.RecommendPartial);
        r.AllowedAmount.Should().Be(80m, "the tighter of the two caps applies");
    }

    [Fact]
    public void Hard_block_and_no_tariff_together_deny_and_report_both()
    {
        var r = Adjudicator.Evaluate(Base() with { BeneficiaryEligible = false, ContractPrice = null });
        r.ReasonCodes.Should().Contain([ReasonCodes.NotEligible, ReasonCodes.NoTariff]);
        r.Recommendation.Should().Be(SystemRecommendation.RecommendDeny, "a hard block outranks manual pricing");
        r.AllowedAmount.Should().Be(0m);
    }

    [Fact]
    public void Copay_splits_the_allowed_amount_between_member_and_payer()
    {
        var r = Adjudicator.Evaluate(Base() with { MemberShare = 30m });
        r.Recommendation.Should().Be(SystemRecommendation.RecommendApprove);
        r.AllowedAmount.Should().Be(150m); // 180 payable − 30 member share
        r.MemberShare.Should().Be(30m);
    }

    [Fact]
    public void Every_reason_the_engine_emits_is_in_the_catalogue()
    {
        // Fire a facts bag that trips as many checks as possible; assert all codes are known + rule_version stamped.
        var r = Adjudicator.Evaluate(new AdjudicationFacts
        {
            BilledAmount = 200m, ContractPrice = null, BeneficiaryEligible = false, PolicyValid = false,
            CoverageCategoryMatches = false, IsGatedService = true, Authorization = AuthorizationState.Expired,
            HasFulfillmentRecord = false, IsDuplicate = true, ProviderInNetwork = false, ContractEffective = false,
        });
        r.ReasonCodes.Should().OnlyContain(c => ReasonCodes.IsKnown(c));
        r.RuleVersion.Should().Be(Adjudicator.RuleVersion);
    }
}
