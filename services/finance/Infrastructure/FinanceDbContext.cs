using Mersal.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Finance.Infrastructure;

/// <summary>EF Core context for the <c>finance</c> read-model + settlements (phase 10.2). Built from domain events;
/// NEVER joins clinical tables and stores NO diagnosis/clinical column. utilization_fact + settlement/settlement_line
/// + dedupe ledger + export log.</summary>
public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public const string Schema = "finance";

    public DbSet<UtilizationFact> UtilizationFacts => Set<UtilizationFact>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<SettlementLine> SettlementLines => Set<SettlementLine>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<ExportRecord> Exports => Set<ExportRecord>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("finance");
        b.HasDefaultSchema(Schema);

        b.Entity<UtilizationFact>(e =>
        {
            e.ToTable("utilization_fact");
            e.HasKey(x => x.FactId);
            e.Property(x => x.UnitCost).HasColumnType("numeric(14,2)");
            e.Property(x => x.LineCost).HasColumnType("numeric(14,2)");
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Period });
            e.HasIndex(x => new { x.TenantId, x.ProviderId, x.Period });
        });

        b.Entity<Settlement>(e =>
        {
            e.ToTable("settlement");
            e.HasKey(x => x.SettlementId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.Total).HasColumnType("numeric(16,2)");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.SettlementNo).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ProviderId, x.PeriodStart });
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.SettlementId);
        });

        b.Entity<SettlementLine>(e =>
        {
            e.ToTable("settlement_line");
            e.HasKey(x => x.SettlementLineId);
            e.Property(x => x.AgreedUnitPrice).HasColumnType("numeric(14,2)");
            e.Property(x => x.PriceSource).HasColumnName("price_source").HasMaxLength(32);
            e.Property(x => x.LineTotal).HasColumnType("numeric(16,2)");
            e.HasIndex(x => x.SettlementId);
        });

        b.Entity<ProcessedEvent>(e => { e.ToTable("processed_event"); e.HasKey(x => x.EventId); });
        b.Entity<ExportRecord>(e => { e.ToTable("export_record"); e.HasKey(x => x.ExportId); });
        b.Entity<ProcessedRequest>(e => { e.ToTable("processed_request"); e.HasKey(x => x.IdempotencyKey); });
    }
}

/// <summary>HTTP idempotency ledger — a replayed <c>Idempotency-Key</c> on settlement generation returns the prior
/// settlement instead of minting a second financial artifact. RLS-free (keys are opaque + globally unique), like the
/// event dedupe ledger. Distinct from <c>ProcessedEvent</c>, which dedupes inbound domain events by event id.</summary>
public sealed class ProcessedRequest
{
    public string IdempotencyKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public Guid ResultId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
