using Mersal.Emr.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Infrastructure;

/// <summary>EF Core context for the <c>emr</c> schema. Phase 2.3: encounter shell + clinician queue.</summary>
public sealed class EmrDbContext(DbContextOptions<EmrDbContext> options) : DbContext(options)
{
    public const string Schema = "emr";

    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentSlot> AppointmentSlots => Set<AppointmentSlot>();
    public DbSet<ProviderAvailability> ProviderAvailabilities => Set<ProviderAvailability>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Appointment>(e =>
        {
            e.ToTable("appointment");
            e.HasKey(x => x.AppointmentId);
            e.Property(x => x.AppointmentType).HasConversion<string>().HasColumnName("appointment_type");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            // xmin optimistic-concurrency token (feeds the 3.2 If-Match ETag).
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            // At most one active appointment per slot (partial unique index created in SQL).
            e.HasIndex(x => x.SlotId);
            e.HasIndex(x => x.IdempotencyKey);
        });

        b.Entity<AppointmentSlot>(e =>
        {
            e.ToTable("appointment_slot");
            e.HasKey(x => x.SlotId);
            e.HasIndex(x => new { x.ProviderId, x.LocationId, x.SlotStart });
        });

        b.Entity<ProviderAvailability>(e =>
        {
            e.ToTable("provider_availability");
            e.HasKey(x => x.AvailabilityId);
            e.Property(x => x.DayOfWeek).HasConversion<int>().HasColumnName("day_of_week");
            e.Property(x => x.StartTime).HasColumnName("start_time");
            e.Property(x => x.EndTime).HasColumnName("end_time");
        });

        b.Entity<WaitlistEntry>(e =>
        {
            e.ToTable("waitlist_entry");
            e.HasKey(x => x.WaitlistId);
            e.Property(x => x.AppointmentType).HasConversion<string>().HasColumnName("appointment_type");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => new { x.ProviderId, x.LocationId, x.Status });
        });

        b.Entity<ProcessedRequest>(e =>
        {
            e.ToTable("processed_request");
            e.HasKey(x => x.IdempotencyKey);
        });

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
