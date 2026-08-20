using Mersal.Audit.Client;
using Mersal.Audit.Domain;
using Mersal.Audit.Infrastructure;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Identity & access (defense in depth with Kong): validate tokens + MFA at the service. ---
builder.Services.AddHbmpAuthentication(builder.Configuration);

// --- Audit infrastructure: DB store, WORM, RabbitMQ ingest, periodic verifier. ---
builder.Services.AddAuditInfrastructure(builder.Configuration);

// The service emits its own audit events (e.g. audit.read) via the client. That emit used to land in the
// in-memory outbox — a ConcurrentDictionary nothing drains — on a rationale ("until libs/events provides
// the durable outbox") that expired when 16.2 shipped it. The flag was not environment-gated, so in
// production too every record of who read the audit log was held in process memory and dropped on the
// next restart. 19-audit-strategy makes audit reads auditable; a buffer that forgets is not an audit.
//
// The sink is the broker-direct one rather than a transactional outbox because audit.read accompanies a
// READ: there is no business transaction for it to be atomic with, and this service's least-privilege
// `hbmp_audit` role deliberately cannot run the DDL an outbox table would need. Publishing to
// audit.events puts the event through the same single write path as every other service's — its own
// consumer ingests it, hash-chained and WORM-mirrored. DirectAuditSink fails closed: a publish failure
// logs Critical and rethrows, so a read of the audit log fails rather than completing unaudited.
builder.Services.AddHbmpAuditClient("audit-service");
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpEventPublisher();
builder.Services.AddHbmpDirectAuditSink();

// --- Observability: OpenTelemetry traces → Tempo (OTLP), correlation shared with audit. ---
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("audit-service"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// 18.B2 moved audit-service onto its own least-privilege `hbmp_audit` login role. That role is
// deliberately NOT the owner of schema `audit` — audit_event is FORCE ROW LEVEL SECURITY and the
// REVOKE of UPDATE/DELETE in 0002 is only meaningful while the writer cannot re-grant itself. So
// audit-service can no longer run its own DDL: `CREATE SCHEMA IF NOT EXISTS audit` in 0001 performs
// the CREATE-on-database ACL check even when the schema already exists, which crash-looped the
// service on every boot with `42501: permission denied for database hbmp`.
//
// Migrations are applied out of band by tools/ci/apply-migrations.sh, under a role that owns the
// schema — the same path CI uses (.github/workflows/backend-ci.yml) and the same way the other
// seventeen services have always been migrated. audit-service was the only one migrating at startup.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health/ready");
app.MapGet("/health/live", () => Results.Ok("live")).AllowAnonymous();

// -----------------------------------------------------------------------------------------------
// Audit READ API. Reads are restricted to Security/Compliance/DPO and are THEMSELVES audited
// (audit.read) — 19-audit-strategy.md §10. No write endpoint exists: the only write path is the
// RabbitMQ consumer (append-only ingest).
// -----------------------------------------------------------------------------------------------
var reads = app.MapGroup("/api/v1/audit").RequireAuthorization(HbmpPolicies.Scope("audit:read"));

reads.MapGet("/events/{entityType}/{entityId}", async (
    string entityType, string entityId,
    AuditDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
{
    var rows = await db.AuditEvents.AsNoTracking()
        .Where(x => x.EntityType == entityType && x.EntityId == entityId)
        .OrderBy(x => x.OccurredAt)
        .Take(500)
        .ToListAsync(ct);

    // Reading the audit store is itself an audited action.
    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = "audit_event", EntityId = $"{entityType}/{entityId}",
        Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
        DecisionOutcome = $"returned {rows.Count} records",
    }, ct);

    // 29.1 (design 45 §1 (c)) — resolve identifiers renamed since these rows were written, WITHOUT touching
    // them. The stored value is returned alongside the display value: the row's bytes are what its
    // record_hash covers, so an investigator reconciling a record against the chain needs to see them. The
    // rows themselves are never updated — that would break the chain the trail's evidential value rests on.
    return Results.Ok(rows.ConvertAll(r =>
    {
        var e = r.ToDomain();
        return new
        {
            Event = e,
            ActorRoleAsStored = e.ActorRole,
            ActorRoleDisplay = LegacyIdentifierDisplay.Display(e.ActorRole),
            ActorRoleIsRetiredName = LegacyIdentifierDisplay.IsRetired(e.ActorRole),
        };
    }));
});

reads.MapGet("/verify/{partitionKey}", async (string partitionKey, AuditVerifier verifier, CancellationToken ct) =>
{
    var result = await verifier.VerifyPartitionAsync(partitionKey, ct);
    return Results.Ok(result);
});

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

// Exposed for WebApplicationFactory-based integration tests (phase 11 / when Docker is up).
public partial class Program;
