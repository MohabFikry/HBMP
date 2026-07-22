using Microsoft.EntityFrameworkCore;

namespace Mersal.Audit.Infrastructure;

/// <summary>
/// EF Core context for the isolated <c>audit</c> schema. The table is created/maintained by the
/// hand-authored SQL migration (partitioning + INSERT-only grants + RLS need raw SQL); EF here is
/// used for reads and appends only. See Migrations/0001_audit_schema.sql.
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public const string Schema = "audit";

    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<AuditEventRow>(e =>
        {
            e.ToTable("audit_event");
            // Composite PK includes the range-partition column (occurred_at), as PG requires.
            e.HasKey(x => new { x.AuditEventId, x.OccurredAt });

            e.Property(x => x.PartitionKey).HasColumnName("partition_key").IsRequired();
            e.Property(x => x.ServiceName).HasColumnName("service_name").IsRequired();
            e.Property(x => x.SourceService).HasColumnName("source_service").IsRequired();
            e.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.Action).HasColumnName("action").IsRequired();
            e.Property(x => x.Severity).HasColumnName("severity").IsRequired();
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.ActorRole).HasColumnName("actor_role");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.ActorMfa).HasColumnName("actor_mfa");
            e.Property(x => x.BeforeState).HasColumnName("before_state").HasColumnType("jsonb");
            e.Property(x => x.AfterState).HasColumnName("after_state").HasColumnType("jsonb");
            e.Property(x => x.FieldClasses).HasColumnName("field_classes").HasColumnType("text[]");
            e.Property(x => x.DecisionOutcome).HasColumnName("decision_outcome");
            e.Property(x => x.DecisionPolicyId).HasColumnName("decision_policy_id");
            e.Property(x => x.DecisionReasonCode).HasColumnName("decision_reason_code");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.BreakGlass).HasColumnName("break_glass");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
            e.Property(x => x.PrevHash).HasColumnName("prev_hash");
            e.Property(x => x.RecordHash).HasColumnName("record_hash").IsRequired();

            e.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAt });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => new { x.PartitionKey, x.Seq });
        });
    }
}
