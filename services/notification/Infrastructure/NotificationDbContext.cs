using Mersal.Notification.Domain;
using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Notification.Infrastructure;

/// <summary>EF Core context for the <c>notification</c> schema (phase 8.1): notifications (the in-app inbox +
/// email/sms delivery rows), versioned bilingual templates, and the event-dedupe ledger.</summary>
public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public const string Schema = "notification";

    public DbSet<Domain.Notification> Notifications => Set<Domain.Notification>();
    public DbSet<NotificationTemplate> Templates => Set<NotificationTemplate>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox("notification");
        b.HasDefaultSchema(Schema);

        b.Entity<Domain.Notification>(e =>
        {
            e.ToTable("notification");
            e.HasKey(x => x.NotificationId);
            e.Property(x => x.Channel).HasConversion<string>().HasColumnName("channel");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.HasIndex(x => new { x.RecipientUserId, x.CreatedAt });
            e.HasIndex(x => new { x.SourceEventId, x.RecipientUserId, x.Channel }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.Actionable, x.EscalationDueAt, x.EscalatedAt, x.ReadAt });
        });

        b.Entity<NotificationTemplate>(e =>
        {
            e.ToTable("notification_template");
            e.HasKey(x => x.TemplateId);
            e.HasIndex(x => new { x.TemplateKey, x.Locale, x.Version }).IsUnique();
        });

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_event");
            e.HasKey(x => x.EventId);
        });
    }
}
