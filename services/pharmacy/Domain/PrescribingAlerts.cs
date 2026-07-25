namespace Mersal.Pharmacy.Domain;

public enum AlertKind { DrugInteraction, Allergy }

/// <summary>A prescribe-time safety alert (US-033). ADVISORY / non-blocking: the prescriber may proceed with an
/// acknowledged override, which is recorded. Alerts never hard-block submission.</summary>
public sealed record PrescribingAlert(AlertKind Kind, string Severity, string Detail);

/// <summary>The result of screening a prescription: the alerts raised and whether the prescriber acknowledged an
/// override. Pure aggregation so the endpoint (which does the cross-service calls) stays thin and this is unit-
/// testable.</summary>
public sealed class AlertScreening
{
    public List<PrescribingAlert> Alerts { get; } = [];
    public bool HasAlerts => Alerts.Count > 0;

    public void AddInteraction(string severity, string detail) =>
        Alerts.Add(new PrescribingAlert(AlertKind.DrugInteraction, severity, detail));

    public void AddAllergy(string detail) =>
        Alerts.Add(new PrescribingAlert(AlertKind.Allergy, "Allergy", detail));

    /// <summary>When alerts exist the prescriber must acknowledge to proceed (advisory override) — but submission
    /// is never blocked; a missing acknowledgement is simply recorded as un-acknowledged, not a hard stop.</summary>
    public bool OverrideRequired => HasAlerts;
}
