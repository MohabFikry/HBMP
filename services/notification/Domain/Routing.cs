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

            // ── Keyed on what the publishers ACTUALLY send (audit §11.3) ─────────────────────────────────
            //
            // Five routes here were keyed on names no service publishes: `ResultReady`, `RxReady`,
            // `AppointmentReminder`, `AppointmentNoShow`, `OrderLineAvailable`. The nearest publishers were
            // sending `OrderResultUploaded`, `RxApproved`, `AppointmentReminderIssued` and `ApptNoShow` —
            // a vocabulary written on one side and never adopted on the other. Nothing failed; the routes
            // were simply never reached, and a routing table full of unreachable entries reads as a working
            // fan-out.
            //
            // The PUBLISHERS' names win. Renaming them instead would have been a wire-contract change across
            // four services with live consumers, to fix a table only this service reads. The template KEYS
            // (`result.ready`, `rx.ready`, …) are unchanged: those are notification-service's own vocabulary,
            // they are rows in `notification.template`, and renaming them means a migration for no gain.
            // Kept under its designed name — see the note below: there is no publisher to rename it TO.
            ["OrderLineAvailable"] = new("order.line_available", "Available", false, false,
                [new("ordering_doctor", InApp)]),
            ["OrderResultUploaded"] = new("result.ready", "Result ready", true, false,
                [new("ordering_doctor", InApp)]),
            // "Ready for dispensing" is the APPROVED state, not the dispensed one — `RxDispensed` is the
            // event after the pharmacist has already acted, and a notice telling them to act then is noise.
            ["RxApproved"] = new("rx.ready", "Ready for dispensing", false, false,
                [new("beneficiary", InApp), new("pharmacist", InApp)]),
            // Out-of-stock prescription line → the ordering doctor + pharmacist, actionable (re-prescribe/substitute).
            ["RxLineOutOfStock"] = new("rx.out_of_stock", "Out of stock", false, true,
                [new("ordering_doctor", InAppEmail), new("pharmacist", InApp)]),

            // A supervisor asking the registration officer for more information (US-003).
            //
            // Addressed to ONE person, not fanned out to a role: the consumer resolves the recipient from the
            // event, because patient-service is the only place that knows which officer filed the application.
            // A request for information that lands in everybody's inbox lands in nobody's work.
            //
            // Actionable, with no escalation rule. Actionable is true because an unanswered request is the
            // entire failure mode this notification exists to prevent. There is no rule because the escalation
            // target has to be a resolved recipient on the envelope, and the only recipient here is the officer
            // — a rule pointing at a role nobody resolved would be inert config that reads as a working
            // safety net. The queue ageing on the approval worklist is what surfaces a stalled application.
            ["RegistrationInfoRequested"] = new("registration.info_requested", "Information requested",
                Sensitive: true, Actionable: true,
                [new("registration_officer", InAppEmail)]),

            // Appointment reminders / no-show — again keyed on what emr-service publishes.
            ["AppointmentReminderIssued"] = new("appointment.reminder", "Reminder", false, false,
                [new("beneficiary", InAppEmail)]),
            ["ApptNoShow"] = new("appointment.no_show", "No-show", false, false,
                [new("reception", InApp)]),
        };

    /*
     * TWO ROUTES REMAIN UNREACHABLE, AND THEY ARE NOT A NAMING PROBLEM.
     * ================================================================
     * `OrderLineAvailable` and `AuthSlaBreached` were the other two §11.3 named, and unlike the four above
     * there is nothing to rename them TO — no service publishes either fact under any name:
     *
     *   · orders-service's vocabulary is Created / PendingApproval / Activated / Cancelled / LinesConsumed /
     *     Completed / ResultUploaded. Nothing models "a line that was unavailable has become available", so
     *     the notice has no moment to fire at.
     *
     *   · `SlaBreached` is a BOOLEAN computed at decision time (`DecisionRules.SlaBreached`), never an event.
     *     A breach is the absence of a decision, so nothing that happens can publish it — only a sweep over
     *     what has not happened can, which is the same shape as the escalation sweep and does not exist yet.
     *
     * They are left in the table deliberately rather than deleted: the templates exist in both languages, the
     * escalation rule for the SLA breach exists, and the design is right. What is missing is a publisher, and
     * a table with the route removed would make that missing publisher invisible to whoever adds one.
     */

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
