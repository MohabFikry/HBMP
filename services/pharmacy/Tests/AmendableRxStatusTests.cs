using FluentAssertions;
using Mersal.Pharmacy.Domain;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 30.2 — which prescription statuses permit their lines to be cancelled or amended.
///
/// <para>The medication twin of orders' <c>AmendableHeadStatusTests</c>, and it exposed the SAME defect,
/// predating phase 30 on both sides. <see cref="RxStatus.PartiallyDispensed"/> had no transition to
/// <see cref="RxStatus.Cancelled"/>, so <c>POST /prescriptions/{id}/cancel</c> answered 409 for every
/// partly-dispensed prescription — the doctor whose three-line script had had its first drug handed over
/// could not withdraw the other two. That is design 46 §3's opening example, and it was unreachable.</para>
///
/// <para>Two services, two state tables, one omission in each. That is what a rule expressed twice does.</para>
/// </summary>
public class AmendableRxStatusTests
{
    [Fact]
    public void A_partly_dispensed_prescription_can_still_be_cancelled()
    {
        PrescriptionWorkflow.CanCancel(RxStatus.PartiallyDispensed).Should().BeTrue(
            "the undispensed remainder is exactly what amendment is FOR");
    }

    [Theory]
    [InlineData(RxStatus.Draft)]
    [InlineData(RxStatus.Submitted)]
    [InlineData(RxStatus.Approved)]
    [InlineData(RxStatus.PartiallyDispensed)]
    public void An_unfinished_prescription_permits_its_lines_to_change(RxStatus status) =>
        PrescriptionWorkflow.CanAmendLines(status).Should().BeTrue();

    [Theory]
    [InlineData(RxStatus.Rejected)]
    [InlineData(RxStatus.Dispensed)]
    [InlineData(RxStatus.Expired)]
    [InlineData(RxStatus.Cancelled)]
    public void A_finished_prescription_does_not(RxStatus status) =>
        PrescriptionWorkflow.CanAmendLines(status).Should().BeFalse();
}
