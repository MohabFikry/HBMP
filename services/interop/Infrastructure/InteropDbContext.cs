using Microsoft.EntityFrameworkCore;
using Mersal.Events;

namespace Mersal.Interop.Infrastructure;

/// <summary>
/// EF Core context for the <c>interop</c> schema (phase 13). The façade owns NO clinical data — it stores only
/// mapping/idempotency metadata: <see cref="FhirCreateRecord"/> is the FHIR <c>If-None-Exist</c> / Idempotency-Key
/// ledger that makes create translation idempotent (a replayed create returns the prior resource, never a
/// duplicate downstream command). Partner registry + inbound staging (13.2) are added in a later migration.
/// </summary>
public sealed class InteropDbContext(DbContextOptions<InteropDbContext> options) : DbContext(options)
{
    public const string Schema = "interop";

    public DbSet<FhirCreateRecord> FhirCreates => Set<FhirCreateRecord>();
    public DbSet<IntegrationPartnerRecord> Partners => Set<IntegrationPartnerRecord>();
    public DbSet<InboundStagingRecord> Staging => Set<InboundStagingRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddOutbox(Schema);
        b.HasDefaultSchema(Schema);

        b.Entity<FhirCreateRecord>(e =>
        {
            e.ToTable("fhir_create");
            e.HasKey(x => x.DedupeKey);
            e.Property(x => x.DedupeKey).HasColumnName("dedupe_key");
            e.Property(x => x.ResourceType).HasColumnName("resource_type");
            e.Property(x => x.CreatedResourceId).HasColumnName("created_resource_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StatusCode).HasColumnName("status_code");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // 13.2 — partner registry (config only, no PHI). Enablement is DPIA-gated.
        b.Entity<IntegrationPartnerRecord>(e =>
        {
            e.ToTable("integration_partner");
            e.HasKey(x => x.PartnerId);
            e.Property(x => x.PartnerId).HasColumnName("partner_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Direction).HasColumnName("direction");
            e.Property(x => x.Transport).HasColumnName("transport");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Dpia).HasColumnName("dpia_status");
            e.Property(x => x.DataSharingAgreementRef).HasColumnName("data_sharing_agreement_ref");
            e.Property(x => x.CrossBorder).HasColumnName("cross_border");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        // 13.2 — inbound quarantine/staging. A message lands here first; the ACL maps it, or it stays quarantined.
        // Nothing here is ever promoted to a core table directly — only internal domain events are emitted.
        b.Entity<InboundStagingRecord>(e =>
        {
            e.ToTable("inbound_staging");
            e.HasKey(x => x.StagingId);
            e.Property(x => x.StagingId).HasColumnName("staging_id");
            e.Property(x => x.PartnerId).HasColumnName("partner_id");
            e.Property(x => x.Format).HasColumnName("format");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.State).HasColumnName("state");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at");
            e.HasIndex(x => new { x.PartnerId, x.State });
        });
    }
}

/// <summary>Registry row for a partner integration (config only). Mirrors <c>PartnerDescriptor</c>.</summary>
public sealed class IntegrationPartnerRecord
{
    public string PartnerId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Direction { get; set; } = default!;
    public string Transport { get; set; } = default!;
    public string Status { get; set; } = "Disabled";
    public string Dpia { get; set; } = "NotStarted";
    public string? DataSharingAgreementRef { get; set; }
    public bool CrossBorder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An inbound message held in staging: either <c>Quarantined</c> (malformed/unmappable/disabled) or
/// <c>Mapped</c> (translated to internal domain events). Never written to a core table directly.</summary>
public sealed class InboundStagingRecord
{
    public Guid StagingId { get; set; }
    public string PartnerId { get; set; } = default!;
    public string Format { get; set; } = default!;
    public string Body { get; set; } = default!;
    public string State { get; set; } = default!;   // "Mapped" | "Quarantined"
    public string? Reason { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Idempotency ledger for FHIR creates: a replayed <c>If-None-Exist</c>/<c>Idempotency-Key</c> returns
/// the resource created the first time, so a double-POST never issues a second native command downstream.</summary>
public sealed class FhirCreateRecord
{
    /// <summary>tenant-scoped dedupe key = "{resourceType}:{tenant}:{ifNoneExist|idempotencyKey}".</summary>
    public string DedupeKey { get; set; } = default!;
    public string ResourceType { get; set; } = default!;
    public string? CreatedResourceId { get; set; }
    public string? TenantId { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
