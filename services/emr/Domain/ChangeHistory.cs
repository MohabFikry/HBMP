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
/// The roster exception's history twin, written by the 0016 trigger. Same shape and same reasoning as
/// <see cref="ProviderAvailabilityHistoryRow"/> — a closure is something a patient will ask about, and "who
/// closed Aswan on the 12th" has to survive the row being edited or withdrawn.
/// </summary>
public sealed class RosterExceptionHistoryRow
{
    public long HistoryId { get; set; }
    public Guid ExceptionId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public string RowSnapshot { get; set; } = default!;
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>One entry of a roster exception's timeline. A withdrawal arrives as an UPDATE whose snapshot has
/// <c>is_deleted: true</c> — which is what withdrawing an exception IS, and why it is not a separate
/// operation.</summary>
public sealed record RosterHistoryView(
    long Sequence,
    string Operation,
    DateTimeOffset RecordedAt,
    string? ActorSubject,
    string? Kind,
    string? DateFrom,
    string? DateTo,
    string? StartTime,
    string? EndTime,
    string? Reason,
    bool Withdrawn)
{
    public static RosterHistoryView From(RosterExceptionHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var doc = JsonDocument.Parse(row.RowSnapshot);
        var r = doc.RootElement;

        return new RosterHistoryView(
            row.HistoryId, row.Operation, row.RecordedAt,
            HistoryJson.Text(r, "updated_by") ?? HistoryJson.Text(r, "created_by"),
            HistoryJson.Text(r, "kind"),
            HistoryJson.Text(r, "date_from"),
            HistoryJson.Text(r, "date_to"),
            HistoryJson.Text(r, "start_time"),
            HistoryJson.Text(r, "end_time"),
            HistoryJson.Text(r, "reason"),
            HistoryJson.Bool(r, "is_deleted"));
    }
}

/// <summary>
/// Readers for a trigger-written snapshot.
///
/// <para>Every read is defensive by design. The snapshot is whatever the table looked like when the row was
/// written, so an entry predating a column simply does not have it — that is a normal history, not a corrupt
/// one, and throwing on it would make the timeline unreadable for exactly the oldest and most interesting
/// entries.</para>
/// </summary>
internal static class HistoryJson
{
    public static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    public static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
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
            HistoryJson.Text(r, "updated_by"),
            HistoryJson.Text(r, "updated_by_name"),
            HistoryJson.Text(r, "start_time"),
            HistoryJson.Text(r, "end_time"),
            HistoryJson.Int(r, "slot_minutes"),
            HistoryJson.Int(r, "max_per_day"),
            HistoryJson.Bool(r, "is_deleted"));
    }
}
