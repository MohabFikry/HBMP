using Mersal.Events;
using Mersal.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Inventory.Infrastructure;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public const string Schema = "inventory";

    public DbSet<Item> Items => Set<Item>();
    public DbSet<BranchItem> BranchItems => Set<BranchItem>();
    public DbSet<StockBatch> Batches => Set<StockBatch>();

    /// <summary>The APPEND-ONLY ledger. There is deliberately no way to update or delete a row here: the
    /// database refuses by trigger and by withheld grant, and a mistake is corrected by a further movement.</summary>
    public DbSet<StockMovement> Movements => Set<StockMovement>();

    /// <summary>The DERIVED balance, mapped to the <c>stock_on_hand</c> VIEW rather than to a column. There is
    /// no <c>quantity_on_hand</c> anywhere on this platform, and mapping the view keyless is what keeps it that
    /// way — EF cannot be asked to write to it.</summary>
    public DbSet<StockOnHandRow> OnHand => Set<StockOnHandRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.AddOutbox("inventory");
        b.HasDefaultSchema(Schema);

        b.Entity<Item>(e =>
        {
            e.ToTable("item");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.Category).HasConversion<string>().HasColumnName("category");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<BranchItem>(e =>
        {
            e.ToTable("branch_item");
            e.HasKey(x => new { x.BranchId, x.ItemId });
        });

        b.Entity<StockBatch>(e =>
        {
            e.ToTable("stock_batch");
            e.HasKey(x => x.BatchId);
            e.HasIndex(x => new { x.ItemId, x.BatchNo }).IsUnique();
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("stock_movement");
            e.HasKey(x => x.MovementId);
            e.Property(x => x.Kind).HasConversion<string>().HasColumnName("kind");
            e.Property(x => x.TransferRef).HasColumnName("transfer_ref");
            e.Property(x => x.CounterpartyBranchId).HasColumnName("counterparty_branch_id");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
        });

        b.Entity<StockOnHandRow>(e =>
        {
            e.HasNoKey();
            e.ToView("stock_on_hand", Schema);
            e.Property(x => x.OnHand).HasColumnName("on_hand");
        });
    }
}

/// <summary>A row of the derived-balance view. Keyless and view-mapped: on-hand is computed, never stored.</summary>
public sealed class StockOnHandRow
{
    public string TenantId { get; set; } = default!;
    public Guid BranchId { get; set; }
    public Guid ItemId { get; set; }
    public Guid? BatchId { get; set; }
    public decimal OnHand { get; set; }
}
