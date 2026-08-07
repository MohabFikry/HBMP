using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Orders.Infrastructure;

/// <summary>EF Core context for the <c>orders</c> schema (investigation orders + lines, phase 4.2).</summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";

    public DbSet<InvestigationOrder> Orders => Set<InvestigationOrder>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderFulfillment> Fulfillments => Set<OrderFulfillment>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();
    public DbSet<ReportAccessRequest> ReportAccessRequests => Set<ReportAccessRequest>();   // 14.7
    public DbSet<ReportAccessGrant> ReportAccessGrants => Set<ReportAccessGrant>();          // 14.7

    /// <summary>
    /// 29.2 — a line's <c>RequestedQuantity</c> defaults to what was ordered.
    ///
    /// <para>Not a fudge to satisfy <c>ck_order_line_ordered_within_requested</c>, but the DEFINITION: for a
    /// line created without a distinct approval step, what was asked for and what may be delivered are the
    /// same number. The two only diverge when an approval NARROWS the entitlement
    /// (<c>ProcedureSessions.ApplyApproval</c>), and that is an explicit act on an existing row.</para>
    ///
    /// <para>Applied here, once, rather than at each writer, because the alternative is a required field that
    /// every present and future call site must remember — and the failure mode when one forgets is a CHECK
    /// violation at save time, i.e. an order a doctor cannot place. A default that is correct by definition
    /// belongs at the choke point; a default that is a guess would not belong anywhere.</para>
    /// </summary>
    private void DefaultRequestedQuantities()
    {
        foreach (var entry in ChangeTracker.Entries<OrderLine>())
        {
            if (entry.State is EntityState.Added && entry.Entity.RequestedQuantity <= 0)
                entry.Entity.RequestedQuantity = entry.Entity.QuantityOrdered;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        DefaultRequestedQuantities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        DefaultRequestedQuantities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("orders");
        b.HasDefaultSchema(Schema);

        b.Entity<InvestigationOrder>(e =>
        {
            e.ToTable("investigation_order");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.OrderType).HasConversion<string>().HasColumnName("order_type");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.Property(x => x.OrderingBranchId).HasColumnName("ordering_branch_id");   // phase 14.4
            e.Property(x => x.SensitivityLevel).HasConversion<string>().HasColumnName("sensitivity_level");   // phase 14.6
            e.Property(x => x.AssignedProviderId).HasColumnName("assigned_provider_id");        // 29.2b
            e.Property(x => x.SharedClinicalContext).HasColumnName("shared_clinical_context");  // 29.2b
            e.Property(x => x.SharedContextBy).HasColumnName("shared_context_by");
            e.Property(x => x.SharedContextAt).HasColumnName("shared_context_at");
            e.Property(x => x.CompletionReport).HasColumnName("completion_report");            // 29.2b
            e.Property(x => x.CompletionReportedBy).HasColumnName("completion_reported_by");
            e.Property(x => x.CompletionReportedAt).HasColumnName("completion_reported_at");
            e.HasIndex(x => x.OrderNo).IsUnique();
            e.HasIndex(x => new { x.BeneficiaryId, x.Status });
            e.HasIndex(x => x.OrderingBranchId);
            e.HasIndex(x => x.IdempotencyKey);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });

        b.Entity<OrderLine>(e =>
        {
            e.ToTable("order_line");
            e.HasKey(x => x.OrderLineId);
            e.Property(x => x.CodeSystem).HasConversion<string>().HasColumnName("code_system");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            // xmin optimistic-concurrency guard: the consume UPDATE only applies when the line hasn't moved,
            // so exactly one racer wins under parallel consume (23 §2 atomic-consume guard).
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.Property(x => x.ExaminationTypeId).HasColumnName("examination_type_id");   // phase 14.6
            e.Property(x => x.SensitivityLevel).HasConversion<string>().HasColumnName("sensitivity_level");   // phase 14.6
            e.Property(x => x.ProcedureTypeCode).HasColumnName("procedure_type_code");   // 29.2
            e.Property(x => x.RequestedQuantity).HasColumnName("requested_quantity");    // 29.2
            e.Ignore(x => x.QuantityRemaining);
            e.HasIndex(x => x.OrderId);
        });

        b.Entity<OrderFulfillment>(e =>
        {
            e.ToTable("order_fulfillment");
            e.HasKey(x => x.FulfillmentId);
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
            e.Property(x => x.RequestHash).HasColumnName("request_hash");
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.OrderLineId);
        });

        b.Entity<ProcessedRequest>(e =>
        {
            e.ToTable("processed_request");
            e.HasKey(x => x.IdempotencyKey);
        });

        // 14.7 — sensitive-result release requests + grants.
        b.Entity<ReportAccessRequest>(e =>
        {
            e.ToTable("report_access_request");
            e.HasKey(x => x.RequestId);
            e.Property(x => x.PurposeCode).HasConversion<string>().HasColumnName("purpose_code");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => x.OrderLineId);
            e.HasIndex(x => x.Status);
        });

        b.Entity<ReportAccessGrant>(e =>
        {
            e.ToTable("report_access_grant");
            e.HasKey(x => x.GrantId);
            e.Property(x => x.PurposeCode).HasConversion<string>().HasColumnName("purpose_code");
            // active-grant lookup: one row per (grantee, line) while not revoked (partial index in SQL).
            e.HasIndex(x => new { x.GranteeUserId, x.OrderLineId });
        });
    }
}

/// <summary>Idempotency ledger row — a replayed Idempotency-Key returns the prior result (no second order).</summary>
public sealed class ProcessedRequest
{
    public string IdempotencyKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public Guid OrderId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
