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
    public DbSet<PrescriptionDispenseWindow> DispenseWindows => Set<PrescriptionDispenseWindow>();   // 29.5
    public DbSet<LineAmendmentRecord> LineAmendments => Set<LineAmendmentRecord>();                  // 30.1
    public DbSet<RefillFrequency> RefillFrequencies => Set<RefillFrequency>();                       // 29.5
    /// <summary>The approval-decision consumer's dedupe ledger — event ids only, no tenant data (0019).</summary>
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    /// <summary>
    /// 30.1 — a new line is version 1 of its own chain unless it was created BY an amendment, which sets the
    /// root explicitly to the chain it continues.
    ///
    /// <para>At the choke point rather than at each writer, for the reason orders' <c>RootLineId</c> default
    /// records: the value is correct by definition, and the failure mode when one call site forgets is a NOT
    /// NULL violation — a prescription a doctor cannot write.</para>
    /// </summary>
    private void DefaultLineRoots()
    {
        foreach (var entry in ChangeTracker.Entries<PrescriptionLine>())
            if (entry.State is EntityState.Added && entry.Entity.RootLineId == Guid.Empty)
                entry.Entity.RootLineId = entry.Entity.PrescriptionLineId;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        DefaultLineRoots();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        DefaultLineRoots();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

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
        // 29.5 — the configurable refill cadence.
        b.Entity<RefillFrequency>(e =>
        {
            e.ToTable("refill_frequency");
            e.HasKey(x => x.Code);
            e.Property(x => x.NameEn).HasColumnName("name_en");
            e.Property(x => x.NameAr).HasColumnName("name_ar");
        });

        // 29.5 — chronic refill windows (design 45 §5).
        b.Entity<PrescriptionDispenseWindow>(e =>
        {
            e.ToTable("prescription_dispense_window");
            e.HasKey(x => x.WindowId);
            e.Property(x => x.PrescriptionLineId).HasColumnName("prescription_line_id");
            e.Property(x => x.ScheduledOpenDate).HasColumnName("scheduled_open_date");
            e.Property(x => x.OpensAt).HasColumnName("opens_at");
            e.Property(x => x.ClosesAt).HasColumnName("closes_at");
            e.Property(x => x.AllocatedQuantity).HasColumnName("allocated_quantity");
            e.Property(x => x.DispensedQuantity).HasColumnName("dispensed_quantity");
            e.Property(x => x.BlockedReason).HasColumnName("blocked_reason");
            e.Property(x => x.MissedAt).HasColumnName("missed_at");
            e.Property(x => x.SupersededByAmendmentId).HasColumnName("superseded_by_amendment_id");  // 30.3
            // xmin: the sweeper and the counter both write this row, and exactly one must win.
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => new { x.PrescriptionLineId, x.WindowNo }).IsUnique();
        });


        b.Entity<PrescriptionLine>(e =>
        {
            e.ToTable("prescription_line");
            e.HasKey(x => x.PrescriptionLineId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            // xmin optimistic-concurrency guard: the dispense UPDATE only applies when the line hasn't moved,
            // so exactly one racer wins under parallel dispense (23 §3 "Pharmacy-specific guards").
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            // 30.1 — the version chain (design 46 §1). The clinical columns are frozen by trg_rx_line_signed;
            // these are the only ones an amendment writes on the ORIGINAL row.
            e.Property(x => x.VersionNo).HasColumnName("version_no");
            e.Property(x => x.SupersedesId).HasColumnName("supersedes_id");
            e.Property(x => x.SupersededById).HasColumnName("superseded_by_id");
            e.Property(x => x.RootLineId).HasColumnName("root_line_id");
            e.Property(x => x.AmendmentReasonCode).HasColumnName("amendment_reason_code");
            e.Property(x => x.AmendmentReasonText).HasColumnName("amendment_reason_text");
            e.Property(x => x.AmendedBy).HasColumnName("amended_by");
            e.Property(x => x.AmendedAt).HasColumnName("amended_at");
            e.Ignore(x => x.QuantityRemaining);
            e.Ignore(x => x.IsTerminal);
            e.HasIndex(x => x.PrescriptionId);
            e.HasIndex(x => new { x.RootLineId, x.VersionNo });
        });

        // 30.1 — the append-only amendment ledger (pharmacy 0013).
        b.Entity<LineAmendmentRecord>(e =>
        {
            e.ToTable("line_amendment");
            e.HasKey(x => x.AmendmentId);
            e.Property(x => x.PrescriptionLineId).HasColumnName("prescription_line_id");
            e.Property(x => x.NewLineId).HasColumnName("new_line_id");
            e.Property(x => x.FromStatus).HasColumnName("from_status");
            e.Property(x => x.ToStatus).HasColumnName("to_status");
            e.Property(x => x.ReasonCode).HasColumnName("reason_code");
            e.Property(x => x.ReasonText).HasColumnName("reason_text");
            e.Property(x => x.AmendedBy).HasColumnName("amended_by");
            e.Property(x => x.AmendedByDisplay).HasColumnName("amended_by_display");
            e.Property(x => x.AmendedAt).HasColumnName("amended_at");
            e.Property(x => x.RequestHash).HasColumnName("request_hash");
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.PrescriptionLineId);
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
            // 29.2 — the CPT code the referral was raised for (design 45 §2).
            e.Property(x => x.RequestedServiceCode).HasColumnName("requested_service_code");
            e.Property(x => x.RequestedServiceCodeSystem).HasColumnName("requested_service_code_system");
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

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_event");
            e.HasKey(x => x.EventId);
        });
    }
}

/// <summary>Transport-level dedupe row — a redelivered broker message is a no-op. No tenant data, so no RLS:
/// it holds event ids and a timestamp. Distinct from <see cref="ProcessedRequest"/>, which dedupes inbound
/// HTTP requests by Idempotency-Key.</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
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
