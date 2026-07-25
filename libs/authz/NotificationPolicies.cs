namespace Mersal.Authz;

/// <summary>
/// The notification policy overlay (phase 8.1). notification-service is an event-driven fan-out engine, not a place
/// business logic lives, so its surface is small: any authenticated user may read THEIR OWN in-app inbox and the
/// delivery status of their own items (tenant-scoped; recipient-ownership is row-filtered in the handler — a user
/// never sees another user's notifications), and mark them read. The <see cref="Ingest"/> action is the
/// system-to-system seam the domain-event routing consumer targets to drive fan-out; it is not a human action.
/// Notification bodies carry NO clinical payload (min-necessary, 11-permission-matrix), so inbox reads are not
/// flagged sensitive; sends of sensitive-context notifications are audited by the dispatcher. See 07 (US-072),
/// 18-security-model.md §4, 19-audit-strategy.md.
/// </summary>
public static class NotificationPolicies
{
    public const string Version = "8.1";

    /// <summary>Read the caller's own in-app inbox + a notification's delivery status; mark read.</summary>
    public const string Read = "notification:read";
    /// <summary>System seam: accept a routed domain event and fan it out to recipients (not a human action).</summary>
    public const string Ingest = "notification:ingest";

    public const string Resource = "notification";

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        // Inbox / delivery / mark-read — ANY authenticated role (empty Roles = any), tenant-scoped. The handler
        // additionally row-filters by recipient == caller, so this is self-service only, never another user's inbox.
        new PolicyRule
        {
            Action = Read, ResourceType = Resource,
            Scopes = Set("notification:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
        // The fan-out seam — service identity holding the ingest scope, tenant-scoped.
        new PolicyRule
        {
            Action = Ingest, ResourceType = Resource,
            Scopes = Set("notification:ingest"),
            RequiredConditions = [AbacConditions.TenantMatch],
        },
    ];

    /// <summary>Full bundle = platform defaults + the notification rules. notification-service authorizes with this.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
