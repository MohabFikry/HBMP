using FluentAssertions;
using Mersal.Pharmacy.Domain;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 30.1 — the medication twin of orders' <c>SupersededLineRollupTests</c>. Same two rules, same two silent
/// failure modes: a superseded line that strands the prescription in PartiallyDispensed so <c>RxDispensed</c>
/// never emits, and a superseded line a counter can still dispense against — handing over the drug, dose or
/// quantity the prescriber corrected.
/// </summary>
public class SupersededLineRollupTests
{
    private static PrescriptionLine Line(RxLineStatus status, decimal prescribed = 20, decimal dispensed = 0) => new()
    {
        PrescriptionLineId = Guid.NewGuid(), DrugId = Guid.NewGuid(),
        QuantityPrescribed = prescribed, QuantityDispensed = dispensed, Status = status,
    };

    private static Prescription Rx(RxStatus status, params PrescriptionLine[] lines) => new()
    {
        PrescriptionId = Guid.NewGuid(), Status = status, Lines = [.. lines],
    };

    [Fact]
    public void A_superseded_line_does_not_hold_the_prescription_open()
    {
        var rx = Rx(RxStatus.PartiallyDispensed,
            Line(RxLineStatus.Superseded),
            Line(RxLineStatus.Dispensed, prescribed: 20, dispensed: 20));

        Dispensing.RecomputePrescriptionStatus(rx).Should().Be(RxStatus.Dispensed);
    }

    [Fact]
    public void A_prescription_whose_every_line_was_superseded_or_cancelled_is_not_Dispensed()
    {
        // Nothing was handed over, so "Dispensed" would be a false statement about a patient's medication.
        var rx = Rx(RxStatus.Approved, Line(RxLineStatus.Superseded), Line(RxLineStatus.Cancelled));

        Dispensing.RecomputePrescriptionStatus(rx).Should().Be(RxStatus.Approved);
    }

    [Fact]
    public void A_superseded_line_that_had_been_partly_dispensed_does_not_report_the_rx_PartiallyDispensed()
    {
        var rx = Rx(RxStatus.Approved, Line(RxLineStatus.Superseded, prescribed: 30, dispensed: 10));

        Dispensing.RecomputePrescriptionStatus(rx).Should().Be(RxStatus.Approved);
    }

    [Fact]
    public void A_superseded_line_can_never_be_dispensed()
    {
        var line = Line(RxLineStatus.Superseded);
        var rx = Rx(RxStatus.Approved, line);

        Dispensing.Validate(rx, line.PrescriptionLineId, 1, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                DateTimeOffset.UtcNow)
            .Should().Be(DispenseError.AlreadyDispensed,
                "dispensing a superseded line hands over the drug the prescriber corrected");
    }

    [Fact]
    public void IsTerminal_covers_every_status_a_line_never_leaves()
    {
        Line(RxLineStatus.Dispensed).IsTerminal.Should().BeTrue();
        Line(RxLineStatus.Cancelled).IsTerminal.Should().BeTrue();
        Line(RxLineStatus.Superseded).IsTerminal.Should().BeTrue();
        Line(RxLineStatus.Active).IsTerminal.Should().BeFalse();
        Line(RxLineStatus.PartiallyDispensed).IsTerminal.Should().BeFalse();
    }
}
