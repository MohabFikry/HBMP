using FluentAssertions;
using Mersal.Pharmacy.Domain;
using Mersal.Time;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 2026-08-09 audit — the pharmacy counter judges dates in CAIRO, not UTC.
///
/// <para>Cairo runs two hours ahead of UTC (three in summer), so between midnight and 02:00 Cairo the UTC
/// date is still yesterday. Everything here happens inside that window, because that is the only time the
/// defect exists — which is exactly why it survived: a suite that pins its clock to mid-morning can never
/// see it, and neither can anybody testing by hand during working hours.</para>
///
/// <para>The two directions cost different things and both are here. A refill window opening TODAY read as
/// not yet open turns a patient away on the one morning they were right to come. A lot expiring TODAY read
/// as still valid hands over expired medicine. The first is the one the audit found; the second is worse.</para>
/// </summary>
public class CairoDateAtTheCounterTests
{
    /// <summary>00:30 Cairo on 15 July 2026 — which is 22:30 UTC on 14 July. Summer, so Cairo is UTC+3.</summary>
    private static readonly DateTimeOffset HalfPastMidnightInCairo =
        new(2026, 7, 14, 21, 30, 0, TimeSpan.Zero);

    [Fact]
    public void The_business_date_at_half_past_midnight_is_todays_Cairo_date()
    {
        // The premise every assertion below rests on, asserted rather than assumed: at this instant the two
        // calendars genuinely disagree. If Egypt ever abolished DST and this became a UTC+2 instant, the
        // window would shift and the tests would quietly stop testing anything — so pin both readings.
        DateOnly.FromDateTime(HalfPastMidnightInCairo.UtcDateTime).Should().Be(new DateOnly(2026, 7, 14));
        BusinessCalendar.DateIn(HalfPastMidnightInCairo).Should().Be(new DateOnly(2026, 7, 15));
    }

    [Fact]
    public void A_lot_expiring_today_is_refused_at_half_past_midnight()
    {
        // Expiry on 15 July, and it is 00:30 on 15 July in the pharmacy. The stock is expired. Judged on the
        // UTC date this reads as 14 July — still in date — and the medicine goes over the counter.
        var rx = Dispensable(out var lineId);

        Dispensing.Validate(rx, lineId, 1, new DateOnly(2026, 7, 15), HalfPastMidnightInCairo)
            .Should().Be(DispenseError.ExpiredLot);
    }

    [Fact]
    public void A_lot_expiring_tomorrow_is_still_dispensable()
    {
        // The other side of the boundary — the rule must not have been made stricter by a day.
        var rx = Dispensable(out var lineId);

        Dispensing.Validate(rx, lineId, 1, new DateOnly(2026, 7, 16), HalfPastMidnightInCairo)
            .Should().Be(DispenseError.None);
    }

    /// <summary>A minimal Submitted prescription with one undispensed line for 10 units.</summary>
    private static Prescription Dispensable(out Guid lineId)
    {
        lineId = Guid.NewGuid();
        var rx = new Prescription
        {
            PrescriptionId = Guid.NewGuid(),
            Status = RxStatus.Approved,
            ExpiresAt = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero),
        };
        rx.Lines.Add(new PrescriptionLine
        {
            PrescriptionLineId = lineId,
            PrescriptionId = rx.PrescriptionId,
            QuantityPrescribed = 10,
            QuantityDispensed = 0,
            Status = RxLineStatus.Active,
        });
        return rx;
    }
}
