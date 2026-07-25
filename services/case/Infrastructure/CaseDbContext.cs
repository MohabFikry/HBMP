using Mersal.Case.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Case.Infrastructure;

/// <summary>EF Core context for the <c>case</c> schema (phase 10.1). Soft-delete + history on all tables; the
/// case_assignment row is the ABAC access anchor. Assignments are never deleted (auditable history); cases/tasks
/// carry a <c>deleted</c> flag (min-necessary soft-delete, never a hard delete of benefit data).</summary>
public sealed class CaseDbContext(DbContextOptions<CaseDbContext> options) : DbContext(options)
{
    public const string Schema = "case";

    public DbSet<CaseFile> Cases => Set<CaseFile>();
    public DbSet<CaseAssignment> Assignments => Set<CaseAssignment>();
    public DbSet<CoordinationTask> Tasks => Set<CoordinationTask>();
    public DbSet<Escalation> Escalations => Set<Escalation>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<CaseFile>(e =>
        {
            e.ToTable("case_file");
            e.HasKey(x => x.CaseId);
            e.Property(x => x.Category).HasConversion<string>().HasColumnName("category");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.Priority).HasConversion<string>().HasColumnName("priority");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.CaseNo).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.BeneficiaryId);
            e.HasQueryFilter(x => !x.Deleted);
            e.HasMany(x => x.Assignments).WithOne().HasForeignKey(a => a.CaseId);
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(t => t.CaseId);
            e.HasMany(x => x.Escalations).WithOne().HasForeignKey(x => x.CaseId);
        });

        b.Entity<CaseAssignment>(e =>
        {
            e.ToTable("case_assignment");
            e.HasKey(x => x.AssignmentId);
            e.HasIndex(x => new { x.CaseManagerId, x.Active });
            e.HasIndex(x => new { x.CaseId, x.Active });
            // At most one ACTIVE assignment per (case, manager) — re-assign after unassign is a new row.
            e.HasIndex(x => new { x.CaseId, x.CaseManagerId })
                .IsUnique()
                .HasFilter("active = true");
        });

        b.Entity<CoordinationTask>(e =>
        {
            e.ToTable("coordination_task");
            e.HasKey(x => x.TaskId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => new { x.CaseId, x.Status });
            e.HasQueryFilter(x => !x.Deleted);
        });

        b.Entity<Escalation>(e =>
        {
            e.ToTable("escalation");
            e.HasKey(x => x.EscalationId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => new { x.CaseId, x.Status });
            e.HasIndex(x => x.RaisedToRole);
        });

        b.Entity<ProcessedRequest>(e =>
        {
            e.ToTable("processed_request");
            e.HasKey(x => x.IdempotencyKey);
        });
    }
}

/// <summary>Idempotency ledger row — a replayed Idempotency-Key returns the prior result (no duplicate case/task).</summary>
public sealed class ProcessedRequest
{
    public string IdempotencyKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public Guid EntityId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
