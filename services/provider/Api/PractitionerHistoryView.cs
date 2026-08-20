using System.Text.Json;
using Mersal.Provider.Domain;

namespace Mersal.Provider.Api;

/// <summary>
/// One entry of a practitioner's change timeline, projected from the 0014 trigger's jsonb snapshot.
///
/// <para><b>Values, not diffs.</b> The client renders "before → after" by comparing an entry with the one
/// before it, so the diff is written once and works for every history on the platform. Computing diffs here
/// would put a second, subtly different notion of "what changed" in each service.</para>
///
/// <para><b>What is deliberately NOT here.</b> The snapshot is the whole row, and the row carries the staff
/// member's names and their user id. Only the administered fields a clinic manager is looking at are
/// projected — licence, expiry, status. A timeline is a record of changes, not a second route to the record
/// itself, and returning the snapshot verbatim would make it one.</para>
/// </summary>
public sealed record PractitionerHistoryView(
    long Sequence,
    string Operation,
    DateTimeOffset RecordedAt,
    string? ActorSubject,
    string? ActorName,
    string? LicenseNo,
    string? LicenseExpiry,
    string? Status,
    bool Deleted)
{
    public static PractitionerHistoryView From(PractitionerHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var doc = JsonDocument.Parse(row.RowSnapshot);
        var r = doc.RootElement;

        return new PractitionerHistoryView(
            row.HistoryId,
            row.Operation,
            row.RecordedAt,
            Text(r, "updated_by") ?? Text(r, "created_by"),
            Text(r, "updated_by_name"),
            Text(r, "license_no"),
            Text(r, "license_expiry"),
            Text(r, "status"),
            Bool(r, "is_deleted"));
    }

    // Defensive by design: the snapshot is whatever the table looked like when the row was written, so an
    // entry predating a column simply does not have it. Throwing on that would make the timeline unreadable
    // for exactly the oldest entries — the ones somebody is digging for.
    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
