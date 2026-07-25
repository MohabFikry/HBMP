using Mersal.Approvals.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Infrastructure;

/// <summary>EF Core context for the <c>approvals</c> schema (authorization aggregate + append-only decision
/// ledger, phase 7). The decision ledger is insert-only: the model never issues an UPDATE/DELETE against it, and
/// the DB enforces the same with a trigger + no UPDATE/DELETE grant (0001 migration).</summary>
public sealed class ApprovalsDbContext(DbContextOptions<ApprovalsDbContext> options) : DbContext(options)
{
    public const string Schema = "approvals";

    public DbSet<Authorization> Authorizations => Set<Authorization>();
    public DbSet<AuthorizationDecision> Decisions => Set<AuthorizationDecision>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Authorization>(e =>
        {
            e.ToTable("authorization");
            e.HasKey(x => x.AuthorizationId);
            e.Property(x => x.Source).HasConversion<string>().HasColumnName("source");
            e.Property(x => x.Priority).HasConversion<string>().HasColumnName("priority");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.ServiceCodes).HasColumnType("jsonb");
            e.Property(x => x.RequestedScope).HasColumnType("jsonb");
            // xmin optimistic-concurrency guard: two reviewers racing to assign/decide the same case → one wins.
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.AuthNo).IsUnique();
            e.HasIndex(x => new { x.Status, x.SlaDueAt });
            e.HasIndex(x => x.BeneficiaryId);
            e.HasIndex(x => x.IdempotencyKey);
            e.HasMany(x => x.Decisions).WithOne().HasForeignKey(d => d.AuthorizationId);
        });

        b.Entity<AuthorizationDecision>(e =>
        {
            e.ToTable("authorization_decision");
            e.HasKey(x => x.DecisionId);
            e.Property(x => x.Decision).HasConversion<string>().HasColumnName("decision");
            e.Property(x => x.ApprovedScope).HasColumnType("jsonb");
            e.HasIndex(x => x.AuthorizationId);
        });

        b.Entity<ProcessedRequest>(e =>
        {
            e.ToTable("processed_request");
            e.HasKey(x => x.IdempotencyKey);
        });
    }
}

/// <summary>Idempotency ledger row — a replayed Idempotency-Key returns the prior result (no second decision).</summary>
public sealed class ProcessedRequest
{
    public string IdempotencyKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public Guid AuthorizationId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
