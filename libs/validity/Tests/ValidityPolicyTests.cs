using FluentAssertions;

namespace Mersal.Validity.Tests;

/// <summary>
/// The rules every service shares about how long a clinical instruction stays actionable.
///
/// <para>These are here rather than in one service's suite because the danger is DRIFT: the value of a
/// missing configuration, the shape of the config key, and the direction a failure degrades in are the three
/// things pharmacy and orders must not come to disagree about.</para>
/// </summary>
public class ValidityPolicyTests
{
    [Fact]
    public void An_unset_period_is_ten_days_and_never_unlimited()
    {
        // The single most important assertion in this file. Every read path falls back through here, and the
        // alternative to a number is a prescription with no end date — the exact state the feature removes.
        ValidityPolicy.DaysFrom(null).Should().Be(10);
        ValidityPolicy.DaysFrom("").Should().Be(10);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0")]        // would expire everything at the moment of writing
    [InlineData("-5")]
    [InlineData("3650")]     // a decade is not an expiry, it is a formality
    [InlineData("1e9")]
    public void A_malformed_or_out_of_range_value_falls_to_the_default_rather_than_throwing(string stored)
    {
        // An operator typo must not stop clinicians prescribing — and must not grant an unbounded window
        // either. It lands on the conservative value.
        ValidityPolicy.DaysFrom(stored).Should().Be(ValidityPolicy.DefaultDays);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("14")]
    [InlineData("365")]
    public void A_configured_value_in_range_is_honoured(string stored)
        => ValidityPolicy.DaysFrom(stored).Should().Be(int.Parse(stored));

    [Fact]
    public void Every_artefact_has_its_own_key_and_no_two_collide()
    {
        var keys = ValidityPolicy.All.Select(ValidityPolicy.KeyFor).ToList();

        keys.Should().OnlyHaveUniqueItems(
            "two artefacts sharing a config key would silently make one of them un-settable");
        keys.Should().AllSatisfy(k => k.Should().StartWith("validity."));
    }

    [Fact]
    public void Expiry_lands_at_the_end_of_the_last_valid_day_not_the_same_clock_time()
    {
        // Written late in the evening, Cairo time.
        var lateEvening = new DateTimeOffset(2026, 8, 3, 19, 50, 0, TimeSpan.Zero);   // 22:50 Cairo
        var earlyMorning = new DateTimeOffset(2026, 8, 3, 6, 5, 0, TimeSpan.Zero);    // 09:05 Cairo

        var a = ValidityPolicy.ExpiryFor(lateEvening, 10);
        var b = ValidityPolicy.ExpiryFor(earlyMorning, 10);

        // Two prescriptions written on the same morning and evening are valid for the same number of DAYS.
        // Expiring the evening one at 22:50 on day 10 would quietly cost it most of a day, for no reason a
        // patient standing at a counter could be told.
        a.Should().Be(b);
    }

    [Fact]
    public void Expiry_is_returned_in_utc()
    {
        // Not cosmetic: Npgsql refuses a non-zero offset for `timestamptz` outright, so a Cairo-offset value
        // fails at the INSERT — which is how this was found.
        var expiry = ValidityPolicy.ExpiryFor(new DateTimeOffset(2026, 8, 3, 19, 50, 0, TimeSpan.Zero), 10);
        expiry.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_ten_day_prescription_written_today_expires_after_the_tenth_day()
    {
        var issued = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);   // 3 Aug, 15:00 Cairo
        var expiry = ValidityPolicy.ExpiryFor(issued, 10);

        // THE DAY OF ISSUE COUNTS AS DAY ONE. Written on 3 August with a ten-day period, it is dispensable
        // on the 3rd through the 12th and lapses at midnight Cairo as the 13th begins — 21:00 UTC on the
        // 12th, because Cairo is UTC+3 in August. Ten days on a calendar, which is what a patient is told
        // at the counter, rather than ten days from a clock time nobody wrote down.
        expiry.Should().Be(new DateTimeOffset(2026, 8, 12, 21, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Bounds_are_stated_where_both_the_UI_and_the_server_can_read_them()
    {
        ValidityPolicy.IsInRange(ValidityPolicy.MinDays).Should().BeTrue();
        ValidityPolicy.IsInRange(ValidityPolicy.MaxDays).Should().BeTrue();
        ValidityPolicy.IsInRange(0).Should().BeFalse();
        ValidityPolicy.IsInRange(ValidityPolicy.MaxDays + 1).Should().BeFalse();
        ValidityPolicy.IsInRange(ValidityPolicy.DefaultDays).Should().BeTrue(
            "a default outside its own bounds would be rejected by the endpoint that writes it");
    }
}
