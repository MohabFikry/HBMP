using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Infrastructure;

/// <summary>EF Core context for the <c>emr</c> schema. Phase 2.3: encounter shell + clinician queue.</summary>
public sealed class EmrDbContext(DbContextOptions<EmrDbContext> options) : DbContext(options)
{
    public const string Schema = "emr";

    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Encounter>(e =>
        {
            e.ToTable("encounter");
            e.HasKey(x => x.EncounterId);
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => x.EncounterNo).IsUnique();
            // Idempotent creation: one encounter per Idempotency-Key (partial unique index created in SQL).
            e.HasIndex(x => x.IdempotencyKey);
        });

        b.Entity<QueueEntry>(e =>
        {
            e.ToTable("queue_entry");
            e.HasKey(x => x.QueueEntryId);
            e.Property(x => x.State).HasConversion<string>().HasColumnName("state");
            e.HasIndex(x => x.EncounterId);
        });
    }
}
