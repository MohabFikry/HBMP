using Mersal.Reporting.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Reporting.Infrastructure;

/// <summary>EF Core context for the <c>reporting</c> read-model (phase 8.2). Projection (fact) tables + the async
/// job + dedupe ledger. This service NEVER writes to source domains and never stores row-level PHI.</summary>
public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public const string Schema = "reporting";

    public DbSet<AuthorizationFact> AuthorizationFacts => Set<AuthorizationFact>();
    public DbSet<PendingAuthorization> PendingAuthorizations => Set<PendingAuthorization>();
    public DbSet<EncounterFact> EncounterFacts => Set<EncounterFact>();
    public DbSet<UtilizationFact> UtilizationFacts => Set<UtilizationFact>();
    public DbSet<CodeCount> CodeCounts => Set<CodeCount>();
    public DbSet<FinancialFact> FinancialFacts => Set<FinancialFact>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<ReportJob> ReportJobs => Set<ReportJob>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("reporting");
        b.HasDefaultSchema(Schema);

        b.Entity<AuthorizationFact>(e =>
        {
            e.ToTable("authorization_fact");
            e.HasKey(x => x.FactId);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Period });
        });
        b.Entity<PendingAuthorization>(e =>
        {
            e.ToTable("pending_authorization");
            e.HasKey(x => x.AuthorizationId);
            e.HasIndex(x => new { x.TenantId, x.Status });
        });
        b.Entity<EncounterFact>(e =>
        {
            e.ToTable("encounter_fact");
            e.HasKey(x => x.FactId);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.Period });
        });
        b.Entity<UtilizationFact>(e =>
        {
            e.ToTable("utilization_fact");
            e.HasKey(x => x.FactId);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Dimension, x.Period });
        });
        b.Entity<CodeCount>(e =>
        {
            e.ToTable("code_count");
            e.HasKey(x => x.FactId);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Kind, x.Period });
        });
        b.Entity<FinancialFact>(e =>
        {
            e.ToTable("financial_fact");
            e.HasKey(x => x.FactId);
            e.Property(x => x.Amount).HasColumnType("numeric(18,2)");
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ServiceLine, x.Period });
        });
        b.Entity<ProcessedEvent>(e => { e.ToTable("processed_event"); e.HasKey(x => x.EventId); });
        b.Entity<ReportJob>(e => { e.ToTable("report_job"); e.HasKey(x => x.JobId); });
    }
}
