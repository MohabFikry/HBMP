using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Accept enum NAMES in request bodies (e.g. vitalType "HR") as well as numbers — the portals send readable
// enum strings. Backward compatible: numeric enum values still deserialize.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("emr-service");
// EMR authorizes with the clinical overlay: treating-relationship on all clinical resources (phase 4.1).
builder.Services.AddHbmpAuthorization(EmrPolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<EmrDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddEmrInfrastructure(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ClinicalGate>();

// Clinical code validation against masterdata-service (fail-closed on writes).
builder.Services.AddHttpClient<IClinicalCodeValidator, HttpClinicalCodeValidator>(c =>
    c.BaseAddress = new Uri(builder.Configuration["MasterData:BaseUrl"] ?? "http://masterdata-service:8080"));

// Reminder channels: in-app live now; SMS/WhatsApp stubs behind the same interface (3.3).
builder.Services.AddScoped<IReminderChannel, InAppReminderChannel>();
builder.Services.AddScoped<IReminderChannel, SmsReminderChannelStub>();
builder.Services.AddScoped<IReminderChannel, WhatsAppReminderChannelStub>();
builder.Services.AddScoped<ReminderDispatcher>();

// Visit gate reads member status from eligibility-service (2.1).
builder.Services.AddHttpClient<IMemberStatusProvider, HttpMemberStatusProvider>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Eligibility:BaseUrl"] ?? "http://eligibility-service:8080"));

// 14.4 — active-branch context: the permitted set is resolved from admin-service; a scoped state holder is
// populated per request by the branch-scope middleware below and read by the worklist endpoints.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<BranchScopeState>();
builder.Services.AddHttpClient<IBranchDirectory, HttpBranchDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080"));

// 18.C2 (audit R2 W7 / FR-BRN-026-027) — doctor↔branch validation. provider-service has exposed the
// serves-branch probe since 14.5 and emr never called it, so a doctor could hold availability at a branch
// they are not assigned to and a patient could be booked into it. Registered as a typed client so the
// booking rules stay testable against the seam without a live sibling.
builder.Services.AddHttpClient<IPractitionerBranchDirectory, HttpPractitionerBranchDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Provider:BaseUrl"] ?? "http://provider-service:8080"));

// 14.5 — flag appointments orphaned when a practitioner stops serving a branch. provider-service publishes
// PractitionerBranchRevoked and nothing consumed it, so bookings already made with that doctor at that branch
// survived unnoticed until the patient turned up.
builder.Services.Configure<PractitionerBranchRevokedOptions>(
    builder.Configuration.GetSection(PractitionerBranchRevokedOptions.SectionName));
builder.Services.AddHostedService<PractitionerBranchRevokedConsumer>();

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("emr-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
// 21.4 — the THIRD gate, asked LAST: after authorization, before execution (design 40 §4). Placed here
// rather than on each route group because the clinical record is one programme, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.Emr);
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

// 14.4 — resolve the active-branch context per request (design 37 §3). BranchScoped callers are narrowed to
// a validated active branch; an X-Active-Branch outside the permitted set is rejected 403 + audited
// (THE INVARIANT: never trust the header). Member/provider-scoped callers are branch-unrestricted.
app.Use(async (ctx, next) =>
{
    var principal = ctx.RequestServices.GetRequiredService<IHbmpPrincipalAccessor>().Principal;
    if (principal is not null && ctx.Request.Path.StartsWithSegments("/api/v1"))
    {
        var header = ctx.Request.Headers[BranchHeaders.ActiveBranch].FirstOrDefault();
        var directory = ctx.RequestServices.GetRequiredService<IBranchDirectory>();
        var state = await BranchScopeResolver.ResolveAsync(principal, header, directory, ctx.RequestAborted);
        if (state.Denied)
        {
            var audit = ctx.RequestServices.GetRequiredService<IAuditClient>();
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "branch_scope", EntityId = header ?? "(none)", Action = AuditAction.Grant,
                ActorUserId = principal.Subject, TenantId = principal.TenantId, ActorMfa = principal.MfaSatisfied,
                DecisionOutcome = "BranchScopeDenied", DecisionReasonCode = "branch-not-permitted", Severity = AuditSeverity.High,
            }, ctx.RequestAborted);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { title = "branch-not-permitted", detail = "the requested active branch is not in your permitted set" });
            return;
        }
        ctx.RequestServices.GetRequiredService<BranchScopeState>().Context = state.Context;
        if (state.Context.ActiveBranchId is { } a) ctx.Response.Headers["X-Active-Branch"] = a.ToString();
    }
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "emr-service" })).AllowAnonymous();

var v1 = app.MapGroup("/api/v1/encounters").RequireAuthorization(HbmpPolicies.Scope("encounter:write"));

// POST /encounters — status-gated visit creation (US-011). Blocked for non-Active (nothing persisted);
// Active → encounter shell + clinician queue entry. Idempotent on Idempotency-Key.
v1.MapPost("", async (
    CreateEncounterRequest req, HttpRequest http,
    IMemberStatusProvider status, EmrDbContext db, EncounterNoIssuer encounterNos,
    IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, BranchScopeState branchScope,
    TimeProvider clock, CancellationToken ct) =>
{
    var idem = http.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idem))
        return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");

    // Idempotent replay → return the existing encounter, do not create a second.
    var existing = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.IdempotencyKey == idem, ct);
    if (existing is not null)
        return Results.Ok(EncounterResponse.From(existing));

    var actor = me.Principal?.Subject;

    // A visit started FROM an appointment carries two rules that belong here rather than on the button: this is
    // where an encounter comes into existence, so a caller going straight to POST /encounters is bound by them
    // too. Both are 23 §1 transitions, not UI preferences.
    if (req.AppointmentId is { } fromApptId)
    {
        if (await AppointmentEndpointsShared.DenyIfOutsideBranchAsync(fromApptId, branchScope, db, ct) is { } outOfBranch)
            return outOfBranch;

        var appt = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.AppointmentId == fromApptId, ct);
        if (appt is null)
            return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

        // 1. The patient has to have ARRIVED. Starting a visit for someone still Booked records care for a
        //    person who is not in the building; starting one twice creates a second encounter for one visit.
        if (appt.Status != AppointmentStatus.CheckedIn)
            return Results.Problem(statusCode: 409, title: "appointment-not-checked-in",
                type: "urn:hbmp:transition-denied",
                detail: $"the appointment is {appt.Status}; a visit starts from CheckedIn",
                extensions: new Dictionary<string, object?> { ["appointmentStatus"] = appt.Status.ToString() });

        // 2. It has to be the doctor's OWN appointment. A NULL doctor is a general clinic session that belongs
        //    to whoever is on shift, so it stays open; a named one does not (VisitStartRules).
        if (!VisitStartRules.MayStart(appt, Guid.TryParse(actor, out var callerId) ? callerId : null))
        {
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "appointment", EntityId = fromApptId.ToString(), Action = AuditAction.Decision,
                ActorUserId = actor, DecisionOutcome = "VisitStartDenied", DecisionReasonCode = "not-assigned-doctor",
            }, ct);
            return Results.Problem(statusCode: 403, title: "not-the-assigned-doctor",
                type: "urn:hbmp:not-assigned", detail: "this appointment is assigned to another practitioner");
        }

        // An encounter already open against this appointment IS the visit — return it rather than opening a
        // second one. The Idempotency-Key replay above only catches a repeat of the SAME request.
        var open = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(
            e => e.AppointmentId == fromApptId && e.Status == EncounterStatus.InProgress, ct);
        if (open is not null) return Results.Ok(EncounterResponse.From(open));
    }

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
    return e is null ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found") : Results.Ok(EncounterResponse.From(e));
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
app.MapQueue();
app.MapClinical();
app.MapProfileContext();   // 20.2 — the seam the patient profile's PMH + encounters sections read
app.MapUtilizationFacts();   // 19.4 — encounter COUNTS for utilization; no clinical payload crosses the wire

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
