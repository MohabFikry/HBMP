using FluentAssertions;
using Mersal.Pharmacy.Domain;

namespace Mersal.Pharmacy.Tests;

/// <summary>Phase 6 pure dispensing rules (23-state-machines §3): a partial dispense leaves the remainder available
/// (line + Rx PartiallyDispensed); a full dispense reaches Dispensed; a used line cannot be dispensed again
/// (no-reuse); over-dispense, an expired lot, and a non-dispensable Rx are all refused. No DB needed.</summary>
public class DispensingRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FutureLot = new(2027, 1, 1);

    private static Prescription Rx(RxStatus status, decimal prescribed, out Guid lineId, decimal dispensed = 0,
        RxLineStatus lineStatus = RxLineStatus.Active, DateTimeOffset? expiresAt = null)
    {
        lineId = Guid.NewGuid();
        return new Prescription
        {
            PrescriptionId = Guid.NewGuid(), RxNo = "RX-2026-000001", Status = status, ExpiresAt = expiresAt,
            Lines = [new PrescriptionLine { PrescriptionLineId = lineId, DrugId = Guid.NewGuid(), QuantityPrescribed = prescribed, QuantityDispensed = dispensed, Status = lineStatus }],
        };
    }

    [Fact]
    public void Partial_dispense_leaves_remainder_available()
    {
        var rx = Rx(RxStatus.Approved, 30, out var lineId);
        Dispensing.Validate(rx, lineId, 10, FutureLot, Now).Should().Be(DispenseError.None);

        var line = rx.Lines[0];
        line.QuantityDispensed += 10;
        line.Status = Dispensing.RecomputeLineStatus(line);
        line.Status.Should().Be(RxLineStatus.PartiallyDispensed);
        line.QuantityRemaining.Should().Be(20);
        Dispensing.RecomputePrescriptionStatus(rx).Should().Be(RxStatus.PartiallyDispensed);
    }

    [Fact]
    public void Full_dispense_reaches_dispensed()
    {
        var rx = Rx(RxStatus.PartiallyDispensed, 30, out var lineId, dispensed: 20, lineStatus: RxLineStatus.PartiallyDispensed);
        Dispensing.Validate(rx, lineId, 10, FutureLot, Now).Should().Be(DispenseError.None);

        var line = rx.Lines[0];
        line.QuantityDispensed += 10;
        line.Status = Dispensing.RecomputeLineStatus(line);
        line.Status.Should().Be(RxLineStatus.Dispensed);
        Dispensing.RecomputePrescriptionStatus(rx).Should().Be(RxStatus.Dispensed);
    }

    [Fact]
    public void A_used_line_cannot_be_dispensed_again()
    {
        var rx = Rx(RxStatus.PartiallyDispensed, 10, out var lineId, dispensed: 10, lineStatus: RxLineStatus.Dispensed);
        Dispensing.Validate(rx, lineId, 1, FutureLot, Now).Should().Be(DispenseError.AlreadyDispensed);
    }

    [Fact]
    public void Over_dispense_is_refused()
    {
        var rx = Rx(RxStatus.Approved, 10, out var lineId, dispensed: 8, lineStatus: RxLineStatus.PartiallyDispensed);
        Dispensing.Validate(rx, lineId, 5, FutureLot, Now).Should().Be(DispenseError.OverDispense);
    }

    [Fact]
    public void An_expired_lot_is_refused()
    {
        var rx = Rx(RxStatus.Approved, 10, out var lineId);
        Dispensing.Validate(rx, lineId, 1, new DateOnly(2025, 1, 1), Now).Should().Be(DispenseError.ExpiredLot);
    }

    [Theory]
    [InlineData(RxStatus.Draft)]
    [InlineData(RxStatus.Submitted)]
    [InlineData(RxStatus.Rejected)]
    [InlineData(RxStatus.Cancelled)]
    [InlineData(RxStatus.Dispensed)]
    [InlineData(RxStatus.Expired)]
    public void A_non_dispensable_prescription_is_refused(RxStatus status)
    {
        var rx = Rx(status, 10, out var lineId);
        Dispensing.Validate(rx, lineId, 1, FutureLot, Now).Should().Be(DispenseError.RxNotDispensable);
    }

    [Fact]
    public void A_prescription_past_its_validity_window_is_refused()
    {
        var rx = Rx(RxStatus.Approved, 10, out var lineId, expiresAt: Now.AddDays(-1));
        Dispensing.Validate(rx, lineId, 1, FutureLot, Now).Should().Be(DispenseError.RxNotDispensable);
    }

    [Fact]
    public void Zero_or_negative_quantity_is_invalid()
    {
        var rx = Rx(RxStatus.Approved, 10, out var lineId);
        Dispensing.Validate(rx, lineId, 0, FutureLot, Now).Should().Be(DispenseError.InvalidQuantity);
    }

    [Fact]
    public void Substitution_is_allowed_only_for_a_policy_approved_alternative()
    {
        var prescribed = Guid.NewGuid();
        var approvedAlt = Guid.NewGuid();
        var offListDrug = Guid.NewGuid();
        IReadOnlyCollection<Guid> approved = [approvedAlt];

        SubstitutionPolicy.IsApproved(prescribed, prescribed, approved).Should().BeTrue("dispensing the prescribed drug is always fine");
        SubstitutionPolicy.IsApproved(prescribed, approvedAlt, approved).Should().BeTrue();
        SubstitutionPolicy.IsApproved(prescribed, offListDrug, approved).Should().BeFalse("an off-list drug must route to approvals");
    }
}
