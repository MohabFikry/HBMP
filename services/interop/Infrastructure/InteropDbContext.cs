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
    }
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
