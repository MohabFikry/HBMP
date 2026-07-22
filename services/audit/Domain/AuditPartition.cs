namespace Mersal.Audit.Domain;

/// <summary>
/// The audit_event table is partitioned monthly (19-audit-strategy.md §4). The hash chain is
/// maintained per partition, so a partition key is derived from the event's UTC occurrence month.
/// </summary>
public static class AuditPartition
{
    /// <summary>Partition key "yyyyMM" (UTC) for an event occurrence.</summary>
    public static string KeyFor(DateTimeOffset occurredAt) =>
        occurredAt.ToUniversalTime().ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
}
