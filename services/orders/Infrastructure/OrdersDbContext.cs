using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Infrastructure;

/// <summary>EF Core context for the <c>orders</c> schema (investigation orders + lines, phase 4.2).</summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";

    public DbSet<InvestigationOrder> Orders => Set<InvestigationOrder>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<ProcessedRequest> ProcessedRequests => Set<ProcessedRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<InvestigationOrder>(e =>
        {
            e.ToTable("investigation_order");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.OrderType).HasConversion<string>().HasColumnName("order_type");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.OrderNo).IsUnique();
            e.HasIndex(x => new { x.BeneficiaryId, x.Status });
            e.HasIndex(x => x.IdempotencyKey);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });

        b.Entity<OrderLine>(e =>
        {
            e.ToTable("order_line");
            e.HasKey(x => x.OrderLineId);
            e.Property(x => x.CodeSystem).HasConversion<string>().HasColumnName("code_system");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => x.OrderId);
        });

        b.Entity<ProcessedRequest>(e =>
        {
            e.ToTable("processed_request");
            e.HasKey(x => x.IdempotencyKey);
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
