using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("emr-service");
builder.Services.AddHbmpAuthorization();
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddEmrInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

// Visit gate reads member status from eligibility-service (2.1).
builder.Services.AddHttpClient<IMemberStatusProvider, HttpMemberStatusProvider>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Eligibility:BaseUrl"] ?? "http://eligibility-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("emr-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "emr-service" })).AllowAnonymous();

var v1 = app.MapGroup("/api/v1/encounters").RequireAuthorization(HbmpPolicies.Scope("encounter:write"));

// POST /encounters — status-gated visit creation (US-011). Blocked for non-Active (nothing persisted);
// Active → encounter shell + clinician queue entry. Idempotent on Idempotency-Key.
v1.MapPost("", async (
    CreateEncounterRequest req, HttpRequest http,
    IMemberStatusProvider status, EmrDbContext db, EncounterNoIssuer encounterNos,
    IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
{
    var idem = http.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idem))
        return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");

    // Idempotent replay → return the existing encounter, do not create a second.
    var existing = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.IdempotencyKey == idem, ct);
    if (existing is not null)
        return Results.Ok(EncounterResponse.From(existing));

    var actor = me.Principal?.Subject;
    var memberStatus = await status.GetStatusAsync(req.BeneficiaryId, http.Headers.Authorization.ToString(), ct);

    // Gate: unknown member or non-Active status → blocked, nothing persisted (audited).
    var gate = memberStatus is null
        ? new GateResult(false, "Unknown member — verify registration before starting a visit.")
        : VisitGate.Evaluate(memberStatus.Value);

    if (!gate.Allowed)
    {
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "encounter", EntityId = req.BeneficiaryId.ToString(), Action = AuditAction.Decision,
            ActorUserId = actor, DecisionOutcome = "VisitBlocked",
            DecisionReasonCode = memberStatus?.ToString() ?? "Unknown",
        }, ct);
        return Results.Problem(statusCode: 422, title: "visit-blocked", detail: gate.Guidance,
            type: "urn:hbmp:visit-blocked",
            extensions: new Dictionary<string, object?> { ["memberStatus"] = memberStatus?.ToString() });
    }

    // Allowed: create the encounter shell + queue entry (transactional), emit events.
    var now = clock.GetUtcNow();
    var encounter = new Encounter
    {
        EncounterId = Guid.NewGuid(),
        EncounterNo = await encounterNos.NextAsync(now.Year, ct),
        BeneficiaryId = req.BeneficiaryId, AppointmentId = req.AppointmentId, ProviderId = req.ProviderId,
        Status = EncounterStatus.InProgress, StartedAt = now, IdempotencyKey = idem, CreatedBy = actor,
    };
    var queueEntry = new QueueEntry
    {
        QueueEntryId = Guid.NewGuid(), EncounterId = encounter.EncounterId,
        BeneficiaryId = req.BeneficiaryId, ProviderId = req.ProviderId, State = QueueState.Waiting, EnqueuedAt = now,
    };
    db.Encounters.Add(encounter);
    db.QueueEntries.Add(queueEntry);
    await db.SaveChangesAsync(ct);

    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = "encounter", EntityId = encounter.EncounterId.ToString(), Action = AuditAction.Create,
        ActorUserId = actor, DecisionOutcome = "EncounterStarted", AfterState = $"{{\"encounterNo\":\"{encounter.EncounterNo}\"}}",
    }, ct);

    if (req.AppointmentId is { } apptId)
        await outbox.EnqueueAsync("ApptCheckedIn", "emr.events", new { appointmentId = apptId, encounterId = encounter.EncounterId }, ct);
    await outbox.EnqueueAsync("EncounterStarted", "emr.events",
        new { encounterId = encounter.EncounterId, encounter.EncounterNo, beneficiaryId = req.BeneficiaryId }, ct);

    return Results.Created($"/api/v1/encounters/{encounter.EncounterId}", EncounterResponse.From(encounter));
});

v1.MapGet("/{id:guid}", async (Guid id, EmrDbContext db, CancellationToken ct) =>
{
    var e = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(x => x.EncounterId == id, ct);
    return e is null ? Results.NotFound() : Results.Ok(EncounterResponse.From(e));
}).RequireAuthorization();

// Clinician worklist — beneficiaries who have checked in and are waiting.
v1.MapGet("/queue", async (EmrDbContext db, CancellationToken ct) =>
{
    var items = await db.QueueEntries.AsNoTracking()
        .Where(q => q.State == QueueState.Waiting)
        .OrderBy(q => q.EnqueuedAt).ToListAsync(ct);
    return Results.Ok(items.Select(QueueItemResponse.From));
}).RequireAuthorization();

app.MapAppointments();

app.Run();

public partial class Program;
