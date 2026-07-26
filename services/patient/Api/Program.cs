using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Events;
using Mersal.Patient.Api;
using Mersal.Patient.Domain;
using Mersal.Patient.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("patient-service");
builder.Services.AddHbmpAuthorization();
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<PatientDbContext>();
builder.Services.AddHbmpOutboxRelay();   // relay staged events (incl. audit) to RabbitMQ
builder.Services.AddPatientInfrastructure(builder.Configuration);
builder.Services.AddScoped<BeneficiaryRegistrar>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("patient-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.AddProblemDetails();
// Accept string enum values (e.g. identifier "type":"UNHCRNo") in request/response JSON.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseHbmpRls(); // bind app.tenant_id / app.provider_id GUCs from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "patient-service" })).AllowAnonymous();

var v1 = app.MapGroup("/api/v1/beneficiaries").RequireAuthorization(HbmpPolicies.Scope("patient:write"));

// POST /beneficiaries — register (Idempotency-Key required); 201 + ETag, or 409 duplicate, or 400.
v1.MapPost("", async (
    RegisterBeneficiaryRequest req, HttpRequest http,
    BeneficiaryRegistrar registrar, PatientDbContext db, IAuditClient audit, IOutbox outbox,
    IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(http.Headers["Idempotency-Key"]))
        return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");

    var actor = me.Principal?.Subject;
    var result = await registrar.RegisterAsync(req, actor, ct);

    switch (result)
    {
        case RegistrationResult.Invalid invalid:
            return Results.ValidationProblem(invalid.Errors.ToDictionary(e => e, e => new[] { e }));

        case RegistrationResult.DuplicateIdentifier dup:
            return Results.Problem(statusCode: 409, title: "duplicate-identifier",
                detail: $"{dup.Type} '{dup.Value}' already exists on beneficiary {dup.ExistingBeneficiaryId}",
                type: "urn:hbmp:duplicate-identifier",
                extensions: new Dictionary<string, object?> { ["existingBeneficiaryId"] = dup.ExistingBeneficiaryId });

        case RegistrationResult.Created created:
            db.Beneficiaries.Add(created.Beneficiary);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary", EntityId = created.Beneficiary.BeneficiaryId.ToString(),
                Action = AuditAction.Create, ActorUserId = actor, FieldClasses = ["identity", "pii"],
            }, ct);
            await outbox.EnqueueAsync("BeneficiaryRegistered", "patient.events",
                new
                {
                    beneficiaryId = created.Beneficiary.BeneficiaryId,
                    status = "Pending",
                    givenName = created.Beneficiary.GivenName,
                    familyName = created.Beneficiary.FamilyName,
                    primaryPhone = created.Beneficiary.Contacts.FirstOrDefault(c => c.IsPrimary)?.Value
                                   ?? created.Beneficiary.Contacts.FirstOrDefault()?.Value,
                    identifiers = created.Beneficiary.Identifiers.Select(i => new { type = i.IdentifierType.ToString(), value = i.IdentifierValue }),
                }, ct);

            return Results.Created($"/api/v1/beneficiaries/{created.Beneficiary.BeneficiaryId}",
                BeneficiaryDto.From(created.Beneficiary));

        default:
            return Results.Problem(statusCode: 500, title: "unexpected");
    }
});

// GET /beneficiaries — search by identifier / name / status (cursor-ish paging).
v1.MapGet("", async (string? identifierType, string? identifierValue, string? name, string? status,
    int? page, int? pageSize, PatientDbContext db, CancellationToken ct) =>
{
    var (p, ps) = (Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 25, 1, 100));
    var q = db.Beneficiaries.AsNoTracking().Where(x => !x.IsDeleted);

    if (!string.IsNullOrWhiteSpace(identifierType) && !string.IsNullOrWhiteSpace(identifierValue)
        && Enum.TryParse<IdentifierType>(identifierType, out var it))
    {
        var norm = IdentifierValidation.Normalize(identifierValue);
        var ids = db.Identifiers.Where(i => i.IdentifierType == it && i.IdentifierValue == norm && !i.IsDeleted).Select(i => i.BeneficiaryId);
        q = q.Where(x => ids.Contains(x.BeneficiaryId));
    }
    if (!string.IsNullOrWhiteSpace(name))
        q = q.Where(x => EF.Functions.ILike(x.GivenName, $"%{name}%") || EF.Functions.ILike(x.FamilyName, $"%{name}%"));
    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<BeneficiaryStatus>(status, out var st))
        q = q.Where(x => x.Status == st);

    var items = await q.OrderBy(x => x.FamilyName).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
    return Results.Ok(new { page = p, pageSize = ps, items = items.Select(BeneficiaryDto.From) });
});

// GET /beneficiaries/{id} — with ETag (row_version).
v1.MapGet("/{id:guid}", async (Guid id, PatientDbContext db, CancellationToken ct) =>
{
    var b = await db.Beneficiaries.AsNoTracking().Include(x => x.Identifiers).Include(x => x.Contacts)
        .FirstOrDefaultAsync(x => x.BeneficiaryId == id && !x.IsDeleted, ct);
    return b is null ? Results.NotFound() : Results.Ok(BeneficiaryDto.From(b));
}).RequireAuthorization();

// ================================================================ REGISTRATION WORKFLOW (1.4, US-003/004)
var reg = app.MapGroup("/api/v1/registrations").RequireAuthorization(HbmpPolicies.Scope("patient:write"));

// Create a registration for an existing (Pending) beneficiary.
reg.MapPost("", async (CreateRegistration req, HttpRequest http, PatientDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(http.Headers["Idempotency-Key"]))
        return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required");
    if (!await db.Beneficiaries.AnyAsync(x => x.BeneficiaryId == req.BeneficiaryId && !x.IsDeleted, ct))
        return Results.NotFound(new { req.BeneficiaryId });
    var now = DateTimeOffset.UtcNow;
    var r = new Registration { RegistrationId = Guid.NewGuid(), BeneficiaryId = req.BeneficiaryId, Status = RegistrationStatus.Pending, CreatedAt = now, UpdatedAt = now };
    db.Registrations.Add(r);
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "registration", EntityId = r.RegistrationId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject }, ct);
    return Results.Created($"/api/v1/registrations/{r.RegistrationId}", new { r.RegistrationId, status = r.Status.ToString() });
});

// Set step data (documents verified / coverage bound / notes).
reg.MapPatch("/{id:guid}", async (Guid id, PatchRegistration req, PatientDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var r = await db.Registrations.FirstOrDefaultAsync(x => x.RegistrationId == id, ct);
    if (r is null) return Results.NotFound();
    var before = $"{{\"documentsVerified\":{r.DocumentsVerified.ToString().ToLowerInvariant()},\"coverageBound\":{r.CoverageBound.ToString().ToLowerInvariant()}}}";
    if (req.DocumentsVerified is { } dv) r.DocumentsVerified = dv;
    if (req.CoverageBound is { } cb) r.CoverageBound = cb;
    if (req.Notes is not null) r.Notes = req.Notes;
    r.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    var after = $"{{\"documentsVerified\":{r.DocumentsVerified.ToString().ToLowerInvariant()},\"coverageBound\":{r.CoverageBound.ToString().ToLowerInvariant()}}}";
    await audit.EmitAsync(new AuditEventDraft { EntityType = "registration", EntityId = r.RegistrationId.ToString(), Action = AuditAction.Update, ActorUserId = me.Principal?.Subject, BeforeState = before, AfterState = after }, ct);
    return Results.Ok(new { r.RegistrationId, status = r.Status.ToString(), r.DocumentsVerified, r.CoverageBound });
});

// Decision: Approve → activate (issue Member No + BeneficiaryActivated); RequestInfo/Reject (reason mandatory).
reg.MapPost("/{id:guid}/decision", async (Guid id, DecisionRequest req, PatientDbContext db, MemberNoIssuer memberNos, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    if (!Enum.TryParse<RegistrationDecision>(req.Decision, ignoreCase: true, out var decision))
        return Results.Problem(statusCode: 400, title: $"invalid decision '{req.Decision}'");
    var r = await db.Registrations.FirstOrDefaultAsync(x => x.RegistrationId == id, ct);
    if (r is null) return Results.NotFound();

    var error = RegistrationRules.ValidateDecision(r, decision, req.Notes);
    if (error is not null) return Results.Problem(statusCode: 422, title: "decision-rejected", detail: error);

    var beneficiary = await db.Beneficiaries.FirstAsync(x => x.BeneficiaryId == r.BeneficiaryId, ct);
    var actor = me.Principal?.Subject;
    r.Status = RegistrationRules.ResultOf(decision);
    r.Notes = req.Notes;
    r.UpdatedAt = DateTimeOffset.UtcNow;

    if (decision == RegistrationDecision.Approve)
    {
        // Transactional activation: beneficiary Active + Member No + MemberNo identifier + BeneficiaryActivated.
        var memberNo = await memberNos.NextAsync(DateTime.UtcNow.Year, ct);
        beneficiary.Status = BeneficiaryStatus.Active;
        beneficiary.MemberNo = memberNo;
        beneficiary.UpdatedBy = actor;
        beneficiary.UpdatedAt = DateTimeOffset.UtcNow;
        db.Identifiers.Add(new BeneficiaryIdentifier
        {
            IdentifierId = Guid.NewGuid(), BeneficiaryId = beneficiary.BeneficiaryId,
            IdentifierType = IdentifierType.MemberNo, IdentifierValue = memberNo, IsPrimary = false,
        });
        await db.SaveChangesAsync(ct);
        await audit.EmitAsync(new AuditEventDraft { EntityType = "beneficiary", EntityId = beneficiary.BeneficiaryId.ToString(), Action = AuditAction.StateChange, ActorUserId = actor, DecisionOutcome = "Activated", AfterState = $"{{\"memberNo\":\"{memberNo}\"}}" }, ct);
        await outbox.EnqueueAsync("BeneficiaryActivated", "patient.events", new { beneficiaryId = beneficiary.BeneficiaryId, memberNo, givenName = beneficiary.GivenName, familyName = beneficiary.FamilyName }, ct);
        return Results.Ok(new { r.RegistrationId, status = r.Status.ToString(), beneficiary.BeneficiaryId, memberNo });
    }

    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "registration", EntityId = r.RegistrationId.ToString(), Action = AuditAction.Decision, ActorUserId = actor, DecisionOutcome = r.Status.ToString(), DecisionReasonCode = req.Notes }, ct);
    return Results.Ok(new { r.RegistrationId, status = r.Status.ToString(), r.Notes });
});

// ================================================================ LIFECYCLE TRANSITIONS (US-004)
v1.MapPost("/{id:guid}/status", async (Guid id, StatusChange req, PatientDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    if (!Enum.TryParse<BeneficiaryStatus>(req.ToStatus, ignoreCase: true, out var to))
        return Results.Problem(statusCode: 400, title: $"invalid status '{req.ToStatus}'");
    var b = await db.Beneficiaries.FirstOrDefaultAsync(x => x.BeneficiaryId == id && !x.IsDeleted, ct);
    if (b is null) return Results.NotFound();

    var error = BeneficiaryLifecycle.Validate(b.Status, to, req.Reason);
    if (error is not null)
    {
        await audit.EmitAsync(new AuditEventDraft { EntityType = "beneficiary", EntityId = id.ToString(), Action = AuditAction.StateChange, ActorUserId = me.Principal?.Subject, DecisionOutcome = "TransitionDenied", DecisionReasonCode = error }, ct);
        return Results.Problem(statusCode: 409, title: "transition-denied", detail: error);
    }
    var from = b.Status;
    b.Status = to; b.UpdatedBy = me.Principal?.Subject; b.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "beneficiary", EntityId = id.ToString(), Action = AuditAction.StateChange, ActorUserId = me.Principal?.Subject, BeforeState = $"{{\"status\":\"{from}\"}}", AfterState = $"{{\"status\":\"{to}\"}}", DecisionReasonCode = req.Reason }, ct);
    await outbox.EnqueueAsync("BeneficiaryStatusChanged", "patient.events", new { beneficiaryId = id, from = from.ToString(), to = to.ToString(), reason = req.Reason }, ct);
    return Results.Ok(new { beneficiaryId = id, from = from.ToString(), to = to.ToString() });
}).RequireAuthorization(HbmpPolicies.Scope("patient:write"));

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
