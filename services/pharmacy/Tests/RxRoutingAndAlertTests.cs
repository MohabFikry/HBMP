using FluentAssertions;
using Mersal.Pharmacy.Domain;

namespace Mersal.Pharmacy.Tests;

/// <summary>Prescription routing (gated/expensive drug → approval, else auto-approve) and the advisory alert
/// model (interaction + allergy alerts are surfaced and require acknowledgement but never block — US-033).</summary>
public class RxRoutingAndAlertTests
{
    private static Prescription Rx(params (Guid drug, decimal qty)[] lines) => new()
    {
        PrescriptionId = Guid.NewGuid(),
        Lines = lines.Select(l => new PrescriptionLine
        {
            PrescriptionLineId = Guid.NewGuid(), DrugId = l.drug, QuantityPrescribed = l.qty,
        }).ToList(),
    };

    [Fact]
    public void Gated_drug_requires_approval()
    {
        var gated = Guid.NewGuid();
        var opts = new RxRoutingOptions { GatedDrugIds = { gated } };
        RxRoutingPolicy.Evaluate(Rx((gated, 1)), opts).RequiresApproval.Should().BeTrue();
    }

    [Fact]
    public void Ordinary_drug_auto_approves()
    {
        var opts = new RxRoutingOptions { GatedDrugIds = { Guid.NewGuid() } };
        var d = RxRoutingPolicy.Evaluate(Rx((Guid.NewGuid(), 1)), opts);
        d.RequiresApproval.Should().BeFalse();
        d.Reason.Should().Be("auto-approve");
    }

    [Fact]
    public void High_cost_requires_approval()
    {
        var drug = Guid.NewGuid();
        var opts = new RxRoutingOptions { HighCostThreshold = 500m, UnitCosts = { [drug] = 300m } };
        RxRoutingPolicy.Evaluate(Rx((drug, 2)), opts).RequiresApproval.Should().BeTrue();  // 600 ≥ 500
    }

    [Fact]
    public void Alerts_are_advisory_override_required_but_not_blocking()
    {
        var s = new AlertScreening();
        s.HasAlerts.Should().BeFalse();
        s.AddInteraction("Major", "warfarin + aspirin");
        s.AddAllergy("penicillin");
        s.HasAlerts.Should().BeTrue();
        s.OverrideRequired.Should().BeTrue();       // acknowledgement expected...
        s.Alerts.Should().HaveCount(2);             // ...but both alerts are simply recorded, never blocking
        s.Alerts[0].Kind.Should().Be(AlertKind.DrugInteraction);
        s.Alerts[1].Kind.Should().Be(AlertKind.Allergy);
    }
}
