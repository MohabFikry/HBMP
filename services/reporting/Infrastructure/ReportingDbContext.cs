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

    // 19.6b — the policy/member analytical read model.
    public DbSet<EnrolmentFact> EnrolmentFacts => Set<EnrolmentFact>();
    public DbSet<MemberUtilizationFact> MemberUtilizationFacts => Set<MemberUtilizationFact>();
    public DbSet<CostFact> CostFacts => Set<CostFact>();
    public DbSet<DimensionLabel> DimensionLabels => Set<DimensionLabel>();

    /// <summary>fact_cost's money columns, hoisted so the model builder does not allocate the array per call.</summary>
    private static readonly string[] MoneyColumns =
        ["ClaimedAmount", "ApprovedAmount", "AdjustedAmount", "NetPayable"];

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

        // ── 19.6b analytics ───────────────────────────────────────────────────────────────────────────────
        // Indexes are shaped by the filter bar, not by the columns: every view filters tenant + period first,
        // then narrows on payer/plan. A dashboard that had to scan a year of facts to answer "this month, this
        // payer" is the slow live query 19.6b forbids.
        b.Entity<EnrolmentFact>(e =>
        {
            e.ToTable("fact_enrolment");
            e.HasKey(x => x.FactId);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Period });
            e.HasIndex(x => new { x.TenantId, x.PayerId, x.Period });
            e.HasIndex(x => new { x.TenantId, x.PolicyPlanId, x.Period });
            e.HasIndex(x => new { x.TenantId, x.EnrollmentId, x.Period });
        });
        b.Entity<MemberUtilizationFact>(e =>
        {
            e.ToTable("fact_utilization");
            e.HasKey(x => x.FactId);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Period });
            e.HasIndex(x => new { x.TenantId, x.PayerId, x.Period });
            e.HasIndex(x => new { x.TenantId, x.BenefitCategoryCode, x.Period });
            e.HasIndex(x => new { x.TenantId, x.Band, x.Period });
            e.Property(x => x.LimitValue).HasColumnType("numeric(18,2)");
            e.Property(x => x.ConsumedValue).HasColumnType("numeric(18,2)");
            e.Property(x => x.Remaining).HasColumnType("numeric(18,2)");
        });
        b.Entity<CostFact>(e =>
        {
            e.ToTable("fact_cost");
            e.HasKey(x => x.FactId);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Period });
            e.HasIndex(x => new { x.TenantId, x.PayerId, x.Period });
            e.HasIndex(x => new { x.TenantId, x.NetworkTierCode, x.Period });
            foreach (var money in MoneyColumns) e.Property(money).HasColumnType("numeric(18,2)");
        });
        b.Entity<DimensionLabel>(e =>
        {
            e.ToTable("dim_label");
            // Composite key: the same id can be two kinds only by accident, but a policy and its default plan
            // sharing an id is not worth a runtime surprise.
            e.HasKey(x => new { x.DimensionId, x.Kind });
            e.HasIndex(x => new { x.TenantId, x.Kind });
        });
    }
}
