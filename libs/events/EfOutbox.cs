using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Mersal.Events;

/// <summary>
/// Durable transactional outbox backed by the caller's EF <see cref="DbContext"/> — replaces the
/// process-local <see cref="InMemoryOutbox"/> (C1). <c>EnqueueRawAsync</c> inserts the row through the
/// SAME context the handler uses, so the event is persisted in Postgres (never a process-local queue).
/// The relay (<see cref="OutboxRelayService"/> + <see cref="EfOutboxReader"/>) drains it at-least-once;
/// consumers dedupe on <c>event_id</c>. Because the platform's handlers commit their business change and
/// THEN enqueue (call sites are preserved per 16.2), the enqueue persists the outbox row on its own
/// SaveChanges — durable across process/broker failure. See ADR-0013.
/// </summary>
public sealed class EfOutbox(DbContext db) : OutboxBase
{
    public override async ValueTask EnqueueRawAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.Set<OutboxMessage>().Add(message);
        await db.SaveChangesAsync(ct);
        // Detach so the long-lived request context doesn't re-track a staged row on a later SaveChanges.
        db.Entry(message).State = EntityState.Detached;
    }
}

/// <summary>
/// Reader for the durable outbox: claims a batch of undispatched rows with <c>FOR UPDATE SKIP LOCKED</c>
/// (so multiple relay instances never double-claim), bumping <c>attempts</c> atomically. Rows past
/// <see cref="EventsOptions.MaxAttempts"/> are quarantined (skipped) — poison messages don't block the
/// stream. Marking processed sets <c>processed_at</c>; a publish failure leaves the row pending for the
/// next pass (attempts already incremented).
/// </summary>
public sealed class EfOutboxReader(DbContext db, IOptions<EventsOptions> options) : IOutboxReader
{
    private readonly int _maxAttempts = options.Value.MaxAttempts;

    private string QualifiedTable()
    {
        var et = db.Model.FindEntityType(typeof(OutboxMessage))
                 ?? throw new InvalidOperationException("OutboxMessage is not mapped — call modelBuilder.AddOutbox(schema) in OnModelCreating.");
        var schema = et.GetSchema() ?? db.Model.GetDefaultSchema() ?? "public";
        var table = et.GetTableName() ?? OutboxSchema.Table;
        return $"\"{schema}\".\"{table}\"";
    }

    public async Task<IReadOnlyList<OutboxMessage>> DequeueBatchAsync(int max, CancellationToken ct = default)
    {
        var tbl = QualifiedTable();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            // Single-statement claim: increment attempts on the rows a SKIP-LOCKED select picks, RETURNING
            // them. No lock is held across the publish (rows are claimed, not row-locked for the batch).
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
UPDATE {tbl} SET attempts = attempts + 1
WHERE event_id IN (
    SELECT event_id FROM {tbl}
    WHERE processed_at IS NULL AND attempts < @maxAttempts
    ORDER BY occurred_at
    FOR UPDATE SKIP LOCKED
    LIMIT @max
)
RETURNING event_id, event_type, destination, payload, correlation_id, occurred_at, processed_at, attempts, last_error;";
            cmd.Parameters.Add(new NpgsqlParameter("maxAttempts", NpgsqlDbType.Integer) { Value = _maxAttempts });
            cmd.Parameters.Add(new NpgsqlParameter("max", NpgsqlDbType.Integer) { Value = max });

            var list = new List<OutboxMessage>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new OutboxMessage
                {
                    EventId = r.GetGuid(0),
                    EventType = r.GetString(1),
                    Destination = r.GetString(2),
                    Payload = r.GetString(3),
                    CorrelationId = await r.IsDBNullAsync(4, ct) ? null : r.GetString(4),
                    OccurredAt = r.GetFieldValue<DateTimeOffset>(5),
                    ProcessedAt = await r.IsDBNullAsync(6, ct) ? null : r.GetFieldValue<DateTimeOffset>(6),
                    Attempts = r.GetInt32(7),
                    LastError = await r.IsDBNullAsync(8, ct) ? null : r.GetString(8),
                });
            }
            return list;
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    public Task MarkProcessedAsync(Guid eventId, CancellationToken ct = default) =>
        ExecAsync($"UPDATE {QualifiedTable()} SET processed_at = now() WHERE event_id = @id", eventId, null, ct);

    public Task MarkFailedAsync(Guid eventId, string error, CancellationToken ct = default) =>
        ExecAsync($"UPDATE {QualifiedTable()} SET last_error = @err WHERE event_id = @id", eventId, error, ct);

    private async Task ExecAsync(string sql, Guid id, string? error, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
            if (error is not null) cmd.Parameters.Add(new NpgsqlParameter("err", NpgsqlDbType.Text) { Value = error });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}
