using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>EF Core context for the <c>claims</c> schema (Phase 10b). Owns the schema exclusively — it never reads
/// another service's tables; cross-context data (tariffs, authorizations, eligibility) comes over the API/events.
/// The schema carries NO clinical column by design (22 §10A minimum-necessary note).</summary>
public sealed class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : DbContext(options)
{
    public const string Schema = "claims";

    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimLine> ClaimLines => Set<ClaimLine>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Claim>(e =>
        {
            e.ToTable("claim");
            e.HasKey(x => x.ClaimId);
            e.Property(x => x.Origin).HasConversion<string>().HasColumnName("origin");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.ClaimNo).IsUnique();
            e.HasIndex(x => new { x.BeneficiaryId, x.Status });
            e.HasIndex(x => new { x.ProviderId, x.ServiceDateFrom });
            e.HasIndex(x => x.Status);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ClaimId);
        });

        b.Entity<ClaimLine>(e =>
        {
            e.ToTable("claim_line");
            e.HasKey(x => x.ClaimLineId);
            e.Property(x => x.FulfillmentType).HasConversion<string>().HasColumnName("fulfillment_type");
            e.Property(x => x.CodeSystem).HasConversion<string>().HasColumnName("code_system");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.SystemRecommendation).HasConversion<string>().HasColumnName("system_recommendation");
            // text[] maps to a Postgres array column.
            e.Property(x => x.ReasonCodes).HasColumnName("reason_codes").HasColumnType("text[]");
            // xmin optimistic-concurrency guard: line decisions (10b.4) land only if the line hasn't moved.
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.ClaimId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.CodeSystem, x.Code });
        });

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_event");
            e.HasKey(x => x.EventId);
        });
    }
}
