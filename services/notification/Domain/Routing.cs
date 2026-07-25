namespace Mersal.Notification.Domain;

/// <summary>One fan-out target: a recipient role and the channels it receives on. The routing consumer resolves the
/// role to concrete recipient user ids + locales (from identity/provider directory) before handing the enriched
/// envelope to the dispatcher — so notification-service stays free of directory business logic.</summary>
public sealed record RouteTarget(string Role, IReadOnlyList<NotificationChannel> Channels);

/// <summary>The notification a domain event maps to: which template renders it, the canonical status text shown in
/// the design-system in-app item, whether it is sensitive (→ send audited), whether it is actionable (→ escalates),
/// and its role→channel targets. See 07 US-072.</summary>
public sealed record NotificationRoute(
    string TemplateKey,
    string StatusText,
    bool Sensitive,
    bool Actionable,
    IReadOnlyList<RouteTarget> Targets);

/// <summary>Time-based escalation rule (07 US-072): if an actionable notification of this event type is not acted on
/// within <see cref="Window"/>, escalate to <see cref="EscalateToRole"/> (supervisor / Medical Director).</summary>
public sealed record EscalationRule(TimeSpan Window, string EscalateToRole, IReadOnlyList<NotificationChannel> Channels);

/// <summary>The event → notification routing map + escalation rules. This is CONFIGURATION, not logic: mapping which
/// role receives which event on which channel, and which unacted events escalate. Everything here is min-necessary
/// and non-clinical.</summary>
public static class RoutingTable
{
    // Channel bundles.
    private static readonly NotificationChannel[] InAppEmail = [NotificationChannel.InApp, NotificationChannel.Email];
    private static readonly NotificationChannel[] InApp = [NotificationChannel.InApp];

    private static readonly IReadOnlyDictionary<string, NotificationRoute> Routes =
        new Dictionary<string, NotificationRoute>(StringComparer.Ordinal)
        {
            // Approval decisions → the requesting provider (in-app + email) + the beneficiary channel.
            ["AuthApproved"] = new("auth.approved", "Approved", Sensitive: true, Actionable: false,
                [new("requesting_provider", InAppEmail), new("beneficiary", InApp)]),
            ["AuthPartiallyApproved"] = new("auth.partially_approved", "Partially approved", true, false,
                [new("requesting_provider", InAppEmail), new("beneficiary", InApp)]),
            ["AuthRejected"] = new("auth.rejected", "Rejected", true, false,
                [new("requesting_provider", InAppEmail), new("beneficiary", InApp)]),
            ["AuthEmergencyApproved"] = new("auth.emergency_approved", "Emergency approved", true, false,
                [new("requesting_provider", InAppEmail)]),
            // InfoRequested is ACTIONABLE → the provider must supply info; escalates to the Medical Director.
            ["AuthInfoRequested"] = new("auth.info_requested", "Information requested", true, Actionable: true,
                [new("requesting_provider", InAppEmail)]),
            // SLA-breaching pending approval → reviewer + Medical Director, actionable.
            ["AuthSlaBreached"] = new("auth.sla_breach", "SLA breach", true, true,
                [new("medical_approval", InAppEmail), new("medical_director", InAppEmail)]),

            // Order / result availability → the ordering doctor (in-app).
            ["OrderLineAvailable"] = new("order.line_available", "Available", false, false,
                [new("ordering_doctor", InApp)]),
            ["ResultReady"] = new("result.ready", "Result ready", true, false,
                [new("ordering_doctor", InApp)]),
            ["RxReady"] = new("rx.ready", "Ready for dispensing", false, false,
                [new("beneficiary", InApp), new("pharmacist", InApp)]),
            // Out-of-stock prescription line → the ordering doctor + pharmacist, actionable (re-prescribe/substitute).
            ["RxLineOutOfStock"] = new("rx.out_of_stock", "Out of stock", false, true,
                [new("ordering_doctor", InAppEmail), new("pharmacist", InApp)]),

            // Appointment reminders / no-show.
            ["AppointmentReminder"] = new("appointment.reminder", "Reminder", false, false,
                [new("beneficiary", InAppEmail)]),
            ["AppointmentNoShow"] = new("appointment.no_show", "No-show", false, false,
                [new("reception", InApp)]),
        };

    private static readonly IReadOnlyDictionary<string, EscalationRule> Escalations =
        new Dictionary<string, EscalationRule>(StringComparer.Ordinal)
        {
            // Unacted info-request escalates to the Medical Director after 24h.
            ["AuthInfoRequested"] = new(TimeSpan.FromHours(24), "medical_director", InAppEmail),
            // Unacted SLA breach escalates to the Medical Director after 2h.
            ["AuthSlaBreached"] = new(TimeSpan.FromHours(2), "medical_director", InAppEmail),
            // Unacted out-of-stock escalates to the pharmacy supervisor after 8h.
            ["RxLineOutOfStock"] = new(TimeSpan.FromHours(8), "pharmacy_supervisor", InAppEmail),
        };

    public static NotificationRoute? Route(string eventType) =>
        Routes.TryGetValue(eventType, out var r) ? r : null;

    public static EscalationRule? Escalation(string eventType) =>
        Escalations.TryGetValue(eventType, out var e) ? e : null;

    public static IReadOnlyCollection<string> KnownEventTypes => (IReadOnlyCollection<string>)Routes.Keys;
}
