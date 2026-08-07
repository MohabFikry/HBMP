using Mersal.Pharmacy.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Pharmacy.Infrastructure;

/// <summary>EF Core context for the <c>pharmacy</c> schema (prescriptions + lines + referrals, phase 4.3).</summary>
public sealed class PharmacyDbContext(DbContextOptions<PharmacyDbContext> options) : DbContext(options)
{
    public const string Schema = "pharmacy";

    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<PrescriptionLine> PrescriptionLines => Set<PrescriptionLine>();
    public DbSet<DispenseEvent> DispenseEvents => Set<DispenseEvent>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<PrescriptionAlert> PrescriptionAlerts => Set<PrescriptionAlert>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();
    public DbSet<PrescriptionValidationRun> PrescriptionValidations => Set<PrescriptionValidationRun>();
    public DbSet<PrescriptionLineOverride> PrescriptionLineOverrides => Set<PrescriptionLineOverride>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("pharmacy");
        b.HasDefaultSchema(Schema);

        b.Entity<Prescription>(e =>
        {
            e.ToTable("prescription");
            e.HasKey(x => x.PrescriptionId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            // Held as a JSON string on the entity; the column is jsonb, and without this EF sends text and
            // Postgres refuses the insert outright.
            e.Property(x => x.DiagnosisSnapshot).HasColumnType("jsonb");
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
            // xmin optimistic-concurrency guard: the dispense UPDATE only applies when the line hasn't moved,
            // so exactly one racer wins under parallel dispense (23 §3 "Pharmacy-specific guards").
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.Ignore(x => x.QuantityRemaining);
            e.HasIndex(x => x.PrescriptionId);
        });

        b.Entity<DispenseEvent>(e =>
        {
            e.ToTable("dispense_event");
            e.HasKey(x => x.DispenseId);
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
            e.Property(x => x.RequestHash).HasColumnName("request_hash");
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.PrescriptionLineId);
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

        // Append-only, both of them: written once and never mutated. Nothing here declares an update path,
        // and the audit story depends on that staying true.
        b.Entity<PrescriptionValidationRun>(e =>
        {
            e.ToTable("prescription_validation");
            e.HasKey(x => x.ValidationId);
            e.Property(x => x.Findings).HasColumnType("jsonb");
            e.Property(x => x.EngineVersion).HasMaxLength(32);
            e.HasIndex(x => x.PrescriptionId);
            e.HasIndex(x => new { x.EncounterId, x.RanAt });
        });

        b.Entity<PrescriptionLineOverride>(e =>
        {
            e.ToTable("prescription_line_override");
            e.HasKey(x => x.OverrideId);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.FindingRef).HasMaxLength(200);
            e.HasIndex(x => x.PrescriptionId);
            e.HasIndex(x => x.LineId);
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
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
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
