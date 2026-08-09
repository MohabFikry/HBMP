using Mersal.Approvals.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Approvals.Infrastructure;

/// <summary>EF Core context for the <c>approvals</c> schema (authorization aggregate + append-only decision
/// ledger, phase 7). The decision ledger is insert-only: the model never issues an UPDATE/DELETE against it, and
/// the DB enforces the same with a trigger + no UPDATE/DELETE grant (0001 migration).</summary>
public sealed class ApprovalsDbContext(DbContextOptions<ApprovalsDbContext> options) : DbContext(options)
{
    public const string Schema = "approvals";

    public DbSet<Authorization> Authorizations => Set<Authorization>();
    public DbSet<AuthorizationDecision> Decisions => Set<AuthorizationDecision>();
    public DbSet<AuthorizationItem> Items => Set<AuthorizationItem>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();
    /// <summary>The fulfilment consumer's dedupe ledger — event ids only, no tenant data (ADR-0034).</summary>
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    /// <summary>The engine's effective-dated routing and SLA rules (ADR-0035 §5).</summary>
    public DbSet<ApprovalRule> Rules => Set<ApprovalRule>();
    /// <summary>The tenant's auto-decision kill switch. NO ROW MEANS OFF (ADR-0035 §5.3).</summary>
    public DbSet<AutoDecisionSwitch> AutoDecisionSwitches => Set<AutoDecisionSwitch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("approvals");
        b.HasDefaultSchema(Schema);

        b.Entity<Authorization>(e =>
        {
            e.ToTable("authorization");
            e.HasKey(x => x.AuthorizationId);
            e.Property(x => x.Kind).HasConversion<string>().HasColumnName("kind");
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
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.AuthorizationId);
        });

        b.Entity<ApprovalRule>(e =>
        {
            e.ToTable("rule");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.Family).HasConversion<string>().HasColumnName("family");
            e.Property(x => x.PredicateJson).HasColumnType("jsonb");
            e.Property(x => x.ActionJson).HasColumnType("jsonb");
            // The order the evaluator reads them in, so the database returns them already sorted and the two
            // cannot disagree about which of two same-priority rules comes first.
            e.HasIndex(x => new { x.TenantId, x.Family, x.Priority, x.RuleId });
        });

        b.Entity<AutoDecisionSwitch>(e =>
        {
            e.ToTable("auto_decision_switch");
            e.HasKey(x => x.TenantId);
        });

        b.Entity<AuthorizationItem>(e =>
        {
            e.ToTable("authorization_item");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.Quantity).HasColumnType("numeric(12,3)");
            // The idempotency anchor: a redelivered dispense under a NEW event id still cannot double-post.
            e.HasIndex(x => new { x.TenantId, x.FulfilmentRef }).IsUnique();
            e.HasIndex(x => x.AuthorizationId);
            e.Ignore(x => x.Substituted);
        });

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_event");
            e.HasKey(x => x.EventId);
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

/// <summary>Transport-level dedupe row — a redelivered broker message is a no-op (ADR-0034). No tenant data,
/// so no RLS: it holds event ids and a timestamp.</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Idempotency ledger row — a replayed Idempotency-Key returns the prior result (no second decision).</summary>
public sealed class ProcessedRequest
{
    public string IdempotencyKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public Guid AuthorizationId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// SHA-256 of the canonical request this key produced (migration 0011).
    /// </summary>
    /// <remarks>
    /// Without it a replay is answered from the key alone, and a reject retried under an approve's key
    /// returned the approval as 200 OK — the reviewer is told the opposite of what they asked for and
    /// nothing records the disagreement. NULL on rows written before the column: unverifiable, so
    /// <see cref="Mersal.Events.IdempotencyKeyRules.Matches"/> treats it as a match.
    /// </remarks>
    public string? RequestHash { get; set; }
}
