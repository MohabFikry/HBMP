using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>EF Core context for the <c>pharmacy</c> schema (prescriptions + lines + referrals, phase 4.3).</summary>
public sealed class PharmacyDbContext(DbContextOptions<PharmacyDbContext> options) : DbContext(options)
{
    public const string Schema = "pharmacy";

    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<PrescriptionLine> PrescriptionLines => Set<PrescriptionLine>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<PrescriptionAlert> PrescriptionAlerts => Set<PrescriptionAlert>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Prescription>(e =>
        {
            e.ToTable("prescription");
            e.HasKey(x => x.PrescriptionId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.RxNo).IsUnique();
            e.HasIndex(x => new { x.BeneficiaryId, x.Status });
            e.HasIndex(x => x.IdempotencyKey);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PrescriptionId);
        });

        b.Entity<PrescriptionLine>(e =>
        {
            e.ToTable("prescription_line");
            e.HasKey(x => x.PrescriptionLineId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => x.PrescriptionId);
        });

        b.Entity<Referral>(e =>
        {
            e.ToTable("referral");
            e.HasKey(x => x.ReferralId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => x.ReferralNo).IsUnique();
            e.HasIndex(x => new { x.BeneficiaryId, x.Status });
            e.HasIndex(x => x.IdempotencyKey);
        });

        b.Entity<PrescriptionAlert>(e =>
        {
            e.ToTable("prescription_alert");
            e.HasKey(x => x.AlertId);
            e.HasIndex(x => x.PrescriptionId);
        });

        b.Entity<ProcessedRequest>(e =>
        {
            e.ToTable("processed_request");
            e.HasKey(x => x.IdempotencyKey);
        });
    }
}

/// <summary>Recorded advisory alert surfaced at prescribe time + whether the prescriber acknowledged an override.</summary>
public sealed class PrescriptionAlert
{
    public Guid AlertId { get; set; }
    public Guid PrescriptionId { get; set; }
    public string Kind { get; set; } = default!;
    public string Severity { get; set; } = default!;
    public string Detail { get; set; } = default!;
    public bool Acknowledged { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
}

public sealed class ProcessedRequest
{
    public string IdempotencyKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public Guid EntityId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
