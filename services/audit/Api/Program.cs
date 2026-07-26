using Mersal.Audit.Client;
using Mersal.Audit.Domain;
using Mersal.Audit.Infrastructure;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Identity & access (defense in depth with Kong): validate tokens + MFA at the service. ---
builder.Services.AddHbmpAuthentication(builder.Configuration);

// --- Audit infrastructure: DB store, WORM, RabbitMQ ingest, periodic verifier. ---
builder.Services.AddAuditInfrastructure(builder.Configuration);

// The service emits its own audit events (e.g. audit.read) via the client; in dev this uses the
// in-memory outbox until libs/events (0.5) provides the durable DB-backed outbox.
builder.Services.AddHbmpAuditClient("audit-service", useInMemoryOutbox: true);

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

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // Apply the hand-authored SQL migrations (partition/grants/RLS) on dev startup.
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
    var migrations = Path.Combine(AppContext.BaseDirectory, "Migrations");
    await SqlFileMigrator.ApplyAsync(db, migrations);
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

    return Results.Ok(rows.ConvertAll(r => r.ToDomain()));
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
