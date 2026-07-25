using FluentAssertions;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>Pure-domain unit tests (no DB): the auto-derive pricing decision (NEVER a guessed price), the reason-code
/// catalogue completeness, and the business-key formats.</summary>
public class ClaimsDomainTests
{
    [Fact]
    public void No_tariff_yields_manual_review_and_a_null_price_never_a_guess()
    {
        var (price, rec, reasons) = AutoDerivePricing.Price(resolvedTariff: null);
        price.Should().BeNull("a missing tariff must never be defaulted, estimated, or carried over");
        rec.Should().Be(SystemRecommendation.RequiresManualReview);
        reasons.Should().ContainSingle().Which.Should().Be(ReasonCodes.NoTariff);
    }

    [Fact]
    public void A_resolved_tariff_prices_the_line_and_leaves_the_recommendation_for_adjudication()
    {
        var (price, rec, reasons) = AutoDerivePricing.Price(resolvedTariff: 125.50m);
        price.Should().Be(125.50m);
        rec.Should().BeNull("the full 9-step adjudication (10b.3) computes the recommendation");
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Every_emitted_reason_code_is_in_the_catalogue_and_vice_versa()
    {
        ReasonCodes.All.Should().HaveCountGreaterThan(10);
        ReasonCodes.All.Should().OnlyContain(c => ReasonCodes.IsKnown(c));
        ReasonCodes.IsKnown("NOT_A_REAL_CODE").Should().BeFalse();
        // The one code the design says a Claims Officer may NEVER raise still exists in the catalogue (a clinical
        // reviewer records it after RouteToClinical).
        ReasonCodes.All.Should().Contain(ReasonCodes.NotMedicallyNecessary);
    }

    [Theory]
    [InlineData(2026, 42, "CLM-2026-000042")]
    [InlineData(2026, 1, "CLM-2026-000001")]
    public void Claim_no_matches_the_regex(int year, int seq, string expected) =>
        ClaimNo.Format(year, seq).Should().Be(expected);

    [Fact]
    public void Batch_no_matches_the_regex() =>
        BatchNo.Format(2026, 7).Should().Be("BAT-2026-000007");
}
