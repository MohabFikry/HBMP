using System.Reflection;
using FluentAssertions;
using Mersal.Claims.Domain;

namespace Mersal.Claims.Tests;

/// <summary>Pure-domain unit tests for the claims KPIs (10b.9, 36 §11): each metric computed on a fixture dataset, plus
/// an assertion that the KPI payload carries no clinical field names.</summary>
public class ClaimsKpiTests
{
    private static readonly Guid ProvA = Guid.NewGuid();
    private static readonly Guid ProvB = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

    private static ClaimsKpi Fixture() => ClaimsKpiCalculator.Compute(
        claims:
        [
            new DecidedClaimFact(ProvA, ClaimStatus.Approved, T0, T0.AddHours(10), 180m, 200m, []),
            new DecidedClaimFact(ProvA, ClaimStatus.Denied, T0, T0.AddHours(20), 0m, 150m, [ReasonCodes.LimitExceeded, ReasonCodes.NoTariff]),
            new DecidedClaimFact(ProvB, ClaimStatus.Denied, T0, T0.AddHours(30), 0m, 100m, [ReasonCodes.LimitExceeded]),
            new DecidedClaimFact(ProvB, ClaimStatus.PartiallyApproved, T0, T0.AddHours(12), 60m, 120m, []),
        ],
        adjustments:
        [
            new AdjustmentFact(AdjustmentType.PriceCorrection, -20m),
            new AdjustmentFact(AdjustmentType.Recovery, -50m),
            new AdjustmentFact(AdjustmentType.Clawback, -30m),
        ],
        reimbursements:
        [
            new ReimbursementFact(ReimbursementMatchMethod.AutoOcr),
            new ReimbursementFact(ReimbursementMatchMethod.AutoOcr),
            new ReimbursementFact(ReimbursementMatchMethod.Manual),
        ],
        unbilled:
        [
            new UnbilledFact(ProvA, 40m), new UnbilledFact(ProvB, 60m),
        ]);

    [Fact]
    public void Tat_is_the_average_submission_to_decision_hours() =>
        Fixture().AverageTatHours.Should().Be(18.0); // (10+20+30+12)/4

    [Fact]
    public void Approval_and_denial_rates_are_fractions_of_decided_claims()
    {
        var k = Fixture();
        k.ApprovalRate.Should().Be(0.5m);  // Approved + PartiallyApproved = 2 of 4
        k.DenialRate.Should().Be(0.5m);    // Denied = 2 of 4
    }

    [Fact]
    public void Top_denial_reasons_are_counted_and_ranked()
    {
        var top = Fixture().TopDenialReasons;
        top[0].Should().Be(new ReasonCount(ReasonCodes.LimitExceeded, 2));
        top.Should().Contain(new ReasonCount(ReasonCodes.NoTariff, 1));
    }

    [Fact]
    public void Adjustment_value_by_type_sums_absolute_deltas()
    {
        var byType = Fixture().AdjustmentValueByType;
        byType.Should().Contain(x => x.Type == "Recovery" && x.TotalAbsValue == 50m);
        byType.Should().Contain(x => x.Type == "PriceCorrection" && x.TotalAbsValue == 20m);
    }

    [Fact]
    public void Provider_variance_league_is_billed_minus_approved_ranked()
    {
        var league = Fixture().ProviderVarianceLeague;
        // ProvA: (200-180)+(150-0)=170 ; ProvB: (100-0)+(120-60)=160
        league[0].Should().Be(new ProviderVariance(ProvA, 170m));
        league[1].Should().Be(new ProviderVariance(ProvB, 160m));
    }

    [Fact]
    public void Ocr_auto_match_rate_is_auto_over_all_matched() =>
        Fixture().OcrAutoMatchRate.Should().Be(0.6667m); // 2 of 3

    [Fact]
    public void Aged_unbilled_and_recovery_outstanding_aggregate()
    {
        var k = Fixture();
        k.AgedUnbilledCount.Should().Be(2);
        k.AgedUnbilledValue.Should().Be(100m);
        k.RecoveryOutstanding.Should().Be(80m); // Recovery 50 + Clawback 30
    }

    [Fact]
    public void The_kpi_payload_contains_no_clinical_field_name()
    {
        string[] forbidden = ["diagnosis", "icd", "clinical", "note", "result", "symptom", "allergy", "soap", "vital"];
        var props = typeof(ClaimsKpi).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name.ToLowerInvariant());
        props.Should().NotContain(p => forbidden.Any(p.Contains));
    }
}
