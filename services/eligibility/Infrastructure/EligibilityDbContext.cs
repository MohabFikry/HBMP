using Mersal.Events;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Infrastructure;

/// <summary>EF Core context for the <c>eligibility</c> schema (read models + snapshots).</summary>
public sealed class EligibilityDbContext(DbContextOptions<EligibilityDbContext> options) : DbContext(options)
{
    public const string Schema = "eligibility";

    public DbSet<MemberProjection> Members => Set<MemberProjection>();
    public DbSet<CoverageProjection> Coverages => Set<CoverageProjection>();
    public DbSet<EligibilitySnapshot> Snapshots => Set<EligibilitySnapshot>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("eligibility");
        b.HasDefaultSchema(Schema);

        b.Entity<MemberProjection>(e =>
        {
            e.ToTable("member_projection");
            e.HasKey(x => x.BeneficiaryId);
            e.HasIndex(x => x.MemberNo);
            e.HasIndex(x => x.NationalId);
            e.HasIndex(x => x.Passport);
            e.HasIndex(x => x.PrimaryPhone);
        });

        b.Entity<CoverageProjection>(e =>
        {
            e.ToTable("coverage_projection");
            e.HasKey(x => x.CoverageId);
            e.HasIndex(x => x.BeneficiaryId);
            e.Property(x => x.LimitsJson).HasColumnType("jsonb");
        });

        b.Entity<EligibilitySnapshot>(e =>
        {
            e.ToTable("eligibility_snapshot");
            e.HasKey(x => x.SnapshotId);
            e.HasIndex(x => new { x.BeneficiaryId, x.BenefitCategory });
            e.Property(x => x.LimitStateJson).HasColumnType("jsonb");
            e.Property(x => x.ReasonsJson).HasColumnType("jsonb");
        });

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_event");
            e.HasKey(x => x.EventId);
        });
    }
}
