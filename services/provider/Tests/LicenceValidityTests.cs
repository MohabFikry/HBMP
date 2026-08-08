using FluentAssertions;
using Mersal.Provider.Domain;

namespace Mersal.Provider.Tests;

/// <summary>
/// 25.3 (design 42 §3) — the licence rule itself. `license_no` and `license_expiry` existed since provider
/// migration 0006 and NOTHING read them, so a doctor whose licence expired last year was still bookable.
/// This is the pure function every caller now shares: provider's probe, emr's slot generation, emr's booking
/// validator, and the expiry sweeper.
/// </summary>
public class LicenceValidityTests
{
    private static readonly DateOnly Expiry = new(2026, 9, 30);

    [Fact]
    public void THE_BOUNDARY_a_licence_is_valid_THROUGH_its_expiry_date()
    {
        // Inclusive, and asserted on both boundary days because this is the assertion that pins the decision.
        // A doctor is not unlicensed on the last day printed on their own certificate; exclusive would move
        // every practitioner's last working day one earlier than the regulator's, and surface as a clinic
        // cancelling a full day of appointments nobody can explain.
        PractitionerLicence.IsValidAt(Expiry, new DateOnly(2026, 9, 29)).Should().BeTrue("the day before");
        PractitionerLicence.IsValidAt(Expiry, Expiry).Should().BeTrue("THE EXPIRY DATE ITSELF — inclusive");
        PractitionerLicence.IsValidAt(Expiry, new DateOnly(2026, 10, 1)).Should().BeFalse("the day after");
    }

    [Fact]
    public void A_NULL_expiry_is_not_expired()
    {
        // "No expiry recorded" is missing data, not a lapsed licence, and collapsing the two would have
        // emptied every clinic's calendar the day this shipped — read, correctly, as an outage rather than a
        // control. The pressure is applied at entry instead: the licence endpoint refuses to store a number
        // without a date.
        PractitionerLicence.IsValidAt(null, new DateOnly(2030, 1, 1)).Should().BeTrue();
        PractitionerLicence.IsEnforceable("LIC-1", null).Should().BeFalse(
            "a licence with no expiry is RECORDED, not ENFORCED, and a worklist must be able to say so");
        PractitionerLicence.IsEnforceable("LIC-1", Expiry).Should().BeTrue();
        PractitionerLicence.IsEnforceable(null, Expiry).Should().BeFalse();
    }

    [Fact]
    public void Days_until_expiry_counts_down_and_then_goes_negative()
    {
        PractitionerLicence.DaysUntilExpiry(Expiry, new DateOnly(2026, 9, 1)).Should().Be(29);
        PractitionerLicence.DaysUntilExpiry(Expiry, Expiry).Should().Be(0);
        PractitionerLicence.DaysUntilExpiry(Expiry, new DateOnly(2026, 10, 5)).Should().Be(-5);
        PractitionerLicence.DaysUntilExpiry(null, Expiry).Should().BeNull();
    }

    [Theory]
    [InlineData("2026-07-02", 90)]
    [InlineData("2026-08-01", 60)]
    [InlineData("2026-08-31", 30)]
    public void The_warning_thresholds_fire_on_exactly_their_day(string on, int expected)
    {
        PractitionerLicence.WarningThresholdCrossedOn(Expiry, DateOnly.Parse(on)).Should().Be(expected);
    }

    [Theory]
    [InlineData("2026-07-03")]   // 89 days
    [InlineData("2026-08-02")]   // 59 days
    [InlineData("2026-09-15")]   // 15 days — between thresholds
    [InlineData("2026-10-15")]   // already lapsed
    public void And_on_no_other_day(string on)
    {
        // Exact-day matching is what makes the sweeper idempotent without a "last warned" column: a second
        // run on the same day emits the same event id and is deduped; a run the next day emits nothing. A
        // `>= threshold` rule would re-warn every day and bury the ones that need acting on.
        PractitionerLicence.WarningThresholdCrossedOn(Expiry, DateOnly.Parse(on)).Should().BeNull();
    }

    [Fact]
    public void The_thresholds_are_the_ones_the_design_names()
    {
        PractitionerLicence.WarningDays.Should().Equal([90, 60, 30]);
    }

    [Fact]
    public void The_refusal_detail_carries_the_expiry_date()
    {
        // The date is the fact that decides the remedy: wait for a renewal, or find cover.
        var id = Guid.NewGuid();
        PractitionerLicence.ExpiredDetail(id, Expiry, new DateOnly(2026, 10, 5))
            .Should().Contain("2026-09-30").And.Contain("2026-10-05").And.Contain(id.ToString());
    }
}
