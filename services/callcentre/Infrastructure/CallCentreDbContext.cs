using System.Text.Json;
using Mersal.CallCentre.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Infrastructure;

/// <summary>EF Core context for the <c>callcentre</c> schema (phase 15.1). Interactions + verification attempts are
/// append/soft-history only (never hard-deleted). The verification row stores only identifier TYPES (a JSON string
/// array) — never values. An idempotency ledger backs the mutating endpoints.</summary>
public sealed class CallCentreDbContext(DbContextOptions<CallCentreDbContext> options) : DbContext(options)
{
    public const string Schema = "callcentre";

    public DbSet<CallInteraction> Interactions => Set<CallInteraction>();
    public DbSet<CallerVerification> Verifications => Set<CallerVerification>();
    public DbSet<AppointmentLink> AppointmentLinks => Set<AppointmentLink>();
    public DbSet<CallProcessedRequest> ProcessedRequests => Set<CallProcessedRequest>();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<CallInteraction>(e =>
        {
            e.ToTable("call_interaction");
            e.HasKey(x => x.InteractionId);
            e.Property(x => x.Direction).HasConversion<string>().HasColumnName("direction");
            e.Property(x => x.ReasonCode).HasConversion<string?>().HasColumnName("reason_code");
            e.Property(x => x.Outcome).HasConversion<string?>().HasColumnName("outcome");
            e.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => x.CallRef).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.AgentUserId, x.StartedAt });
            e.HasIndex(x => x.BeneficiaryId);
            e.HasMany(x => x.Verifications).WithOne().HasForeignKey(v => v.InteractionId);
        });

        b.Entity<CallerVerification>(e =>
        {
            e.ToTable("caller_verification");
            e.HasKey(x => x.VerificationId);
            e.Property(x => x.Result).HasConversion<string>().HasColumnName("result");
            // Store only WHICH identifier TYPES were confirmed — as a JSON string array. NEVER the values.
            e.Property(x => x.VerifiedIdentifierTypes)
                .HasColumnName("verified_identifiers")
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, Json),
                    v => JsonSerializer.Deserialize<List<string>>(v, Json) ?? new List<string>());
            e.HasIndex(x => x.InteractionId);
            e.HasIndex(x => new { x.BeneficiaryId, x.VerifiedAt }).IsDescending(false, true);
        });

        b.Entity<AppointmentLink>(e =>
        {
            e.ToTable("appointment_link");
            e.HasKey(x => x.LinkId);
            e.Property(x => x.Action).HasConversion<string>().HasColumnName("action");
            e.Property(x => x.CancelReason).HasConversion<string?>().HasColumnName("cancel_reason");
            e.HasIndex(x => x.InteractionId);
            e.HasIndex(x => x.AppointmentId);
        });

        b.Entity<CallProcessedRequest>(e =>
        {
            e.ToTable("processed_request");
            e.HasKey(x => x.IdempotencyKey);
        });
    }
}

/// <summary>Idempotency ledger row — a replayed Idempotency-Key returns the prior result (no duplicate interaction).</summary>
public sealed class CallProcessedRequest
{
    public string IdempotencyKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public Guid EntityId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
