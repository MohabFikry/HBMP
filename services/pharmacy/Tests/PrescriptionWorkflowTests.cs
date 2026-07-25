using FluentAssertions;
using Mersal.Pharmacy.Domain;

namespace Mersal.Pharmacy.Tests;

/// <summary>Canonical prescription transitions (23-state-machines §3), dispensability, and business-key formats.</summary>
public class PrescriptionWorkflowTests
{
    [Theory]
    [InlineData(RxStatus.Draft, RxStatus.Submitted, true)]
    [InlineData(RxStatus.Submitted, RxStatus.Approved, true)]
    [InlineData(RxStatus.Submitted, RxStatus.Rejected, true)]
    [InlineData(RxStatus.Approved, RxStatus.Dispensed, true)]
    [InlineData(RxStatus.Approved, RxStatus.PartiallyDispensed, true)]
    [InlineData(RxStatus.Draft, RxStatus.Approved, false)]      // must submit first
    [InlineData(RxStatus.Rejected, RxStatus.Approved, false)]   // terminal
    [InlineData(RxStatus.Dispensed, RxStatus.Cancelled, false)] // terminal
    public void Transition_legality(RxStatus from, RxStatus to, bool legal) =>
        PrescriptionWorkflow.CanTransition(from, to).Should().Be(legal);

    [Theory]
    [InlineData(RxStatus.Draft, true)]
    [InlineData(RxStatus.Submitted, true)]
    [InlineData(RxStatus.Approved, true)]
    [InlineData(RxStatus.Dispensed, false)]
    public void Cancel_guard(RxStatus from, bool canCancel) =>
        PrescriptionWorkflow.CanCancel(from).Should().Be(canCancel);

    [Theory]
    [InlineData(RxStatus.Approved, true)]
    [InlineData(RxStatus.PartiallyDispensed, true)]
    [InlineData(RxStatus.Submitted, false)]   // not dispensable until approved
    [InlineData(RxStatus.Draft, false)]
    public void Dispensable_only_when_approved(RxStatus status, bool dispensable) =>
        PrescriptionWorkflow.IsDispensable(status).Should().Be(dispensable);

    [Fact]
    public void Business_keys_are_formatted()
    {
        RxNo.Format(2026, 7).Should().Be("RX-2026-000007");
        ReferralNo.Format(2026, 7).Should().Be("REF-2026-000007");
    }
}
