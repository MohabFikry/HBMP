using System.Text.Json;

namespace Mersal.Emr.Domain;

/// <summary>
/// A row of the append-only history twin a DB trigger writes for an administered record.
///
/// <para>The platform has kept history this way since provider/0001: an <c>AFTER INSERT OR UPDATE</c> trigger
/// snapshotting <c>to_jsonb(NEW)</c>. The snapshot rather than a column-by-column diff is what makes it
/// survivable — a table that gains a column keeps producing complete history rows without anybody remembering
/// to widen the history table too, which is the failure mode of every hand-maintained audit column set.</para>
///
/// <para><b>This is not the audit trail.</b> The audit chain is hash-linked, tamper-evident, protected, and
/// readable only by Security/Compliance/DPO — it exists to answer an investigator. This exists to answer the
/// person who runs the clinic when they ask who narrowed their Tuesday, and it is readable under the same
/// branch reach as the record it describes. Both are written for every change; neither substitutes for the
/// other.</para>
/// </summary>
public sealed class ProviderAvailabilityHistoryRow
{
    public long HistoryId { get; set; }
    public Guid AvailabilityId { get; set; }
    public string TenantId { get; set; } = default!;
    /// <summary>The trigger's <c>TG_OP</c> — INSERT or UPDATE. A retirement arrives as an UPDATE whose
    /// snapshot has <c>is_deleted: true</c>, which is what a soft delete IS.</summary>
    public string Operation { get; set; } = default!;
    public string RowSnapshot { get; set; } = default!;
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// One entry on the timeline: the administered values as they stood after this change, plus who made it.
///
/// <para><b>Values, not diffs.</b> The client renders "before → after" by comparing an entry with the one
/// before it, which means the diff logic is written once and works for every history on the platform. Computing
/// diffs here would put a second, subtly different notion of "what changed" in each service.</para>
/// </summary>
public sealed record AvailabilityHistoryView(
    long Sequence,
    string Operation,
    DateTimeOffset RecordedAt,
    string? ActorSubject,
    string? ActorName,
    string? StartTime,
    string? EndTime,
    int? SlotMinutes,
    int? MaxPerDay,
    bool Retired)
{
    public static AvailabilityHistoryView From(ProviderAvailabilityHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var doc = JsonDocument.Parse(row.RowSnapshot);
        var r = doc.RootElement;

        return new AvailabilityHistoryView(
            row.HistoryId,
            row.Operation,
            row.RecordedAt,
            Text(r, "updated_by"),
            Text(r, "updated_by_name"),
            Text(r, "start_time"),
            Text(r, "end_time"),
            Int(r, "slot_minutes"),
            Int(r, "max_per_day"),
            Bool(r, "is_deleted"));
    }

    // The snapshot is whatever the table looked like when the row was written, so every read is defensive:
    // entries predating a column simply do not have it, and that is a normal history, not a corrupt one.
    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
