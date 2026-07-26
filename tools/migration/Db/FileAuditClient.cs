using System.Text.Json;
using Mersal.Audit.Client;

namespace Mersal.Migration.Db;

/// <summary>
/// A standalone <see cref="IAuditClient"/> for the migration CLI, which runs outside a service host.
/// Appends each event as a canonical JSON line to a local audit log. In a wired deployment these
/// events are forwarded to audit-service, which applies the WORM hash chain on ingest; here the log
/// is the provenance/audit artifact retained with the run's reconciliation reports.
/// </summary>
public sealed class FileAuditClient(string path) : IAuditClient
{
    private readonly object _gate = new();

    public ValueTask EmitAsync(AuditEventDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow,
            draft.EntityType, draft.EntityId, action = draft.Action.ToString(),
            draft.ActorRole, draft.Purpose, draft.AfterState,
        });
        lock (_gate) { File.AppendAllText(path, line + Environment.NewLine); }
        return ValueTask.CompletedTask;
    }
}
