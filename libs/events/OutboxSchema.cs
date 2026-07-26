using Microsoft.EntityFrameworkCore;

namespace Mersal.Events;

/// <summary>
/// Maps <see cref="OutboxMessage"/> onto a service's <c>&lt;schema&gt;.outbox_message</c> table so the
/// durable <see cref="EfOutbox"/> can stage events in the caller's DbContext. Call from a service's
/// <c>OnModelCreating</c>: <c>b.AddOutbox(Schema);</c>. Columns are named explicitly (snake_case) so the
/// mapping is identical whether or not the host context uses a naming convention.
/// </summary>
public static class OutboxSchema
{
    public const string Table = "outbox_message";

    public static ModelBuilder AddOutbox(this ModelBuilder modelBuilder, string schema)
    {
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable(Table, schema);
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.EventType).HasColumnName("event_type");
            e.Property(x => x.Destination).HasColumnName("destination");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.HasIndex(x => x.OccurredAt).HasFilter("processed_at IS NULL").HasDatabaseName($"ix_{schema}_outbox_pending");
        });
        return modelBuilder;
    }

    /// <summary>
    /// The additive DDL for a service's outbox table. One shared template — the migration file per service
    /// is just <c>Ddl(schema)</c>. Idempotent (IF NOT EXISTS), and grants the NOBYPASSRLS runtime role
    /// when it exists so the relay can drain as <c>hbmp_app</c> (16.4).
    /// </summary>
    public static string Ddl(string schema) => $@"
CREATE TABLE IF NOT EXISTS ""{schema}"".outbox_message (
    event_id       uuid PRIMARY KEY,
    event_type     text NOT NULL,
    destination    text NOT NULL,
    payload        jsonb NOT NULL,
    correlation_id text NULL,
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    processed_at   timestamptz NULL,
    attempts       int NOT NULL DEFAULT 0,
    last_error     text NULL
);
CREATE INDEX IF NOT EXISTS ix_{schema}_outbox_pending
    ON ""{schema}"".outbox_message (occurred_at) WHERE processed_at IS NULL;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON ""{schema}"".outbox_message TO hbmp_app;
    END IF;
END $$;
";
}
