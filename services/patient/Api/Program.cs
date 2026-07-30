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
using Mersal.Time;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("patient-service");
// 18.B3 (S6) — the beneficiary-directory overlay: patient:read as a first-class scope, a Sensitive read rule
// (so the engine audits the allow), and the pii/contact field-class matrix the R1 audit deferred.
builder.Services.AddHbmpAuthorization(PatientPolicies.Bundle(), PatientPolicies.FieldMatrix());
builder.Services.AddScoped<BeneficiaryReadGuard>();
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<PatientDbContext>();
builder.Services.AddHbmpOutboxRelay();   // relay staged events (incl. audit) to RabbitMQ
builder.Services.AddPatientInfrastructure(builder.Configuration);
builder.Services.AddScoped<BeneficiaryRegistrar>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock

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

// Readiness for the probe in infra/helm/rollout/rollout-template.yaml. Process-level only: this reports
// "through startup and able to serve". A dependency check here would pull the pod out of rotation for a
// condition the service already surfaces per-request, turning a partial degradation into a total outage.
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseHbmpRls(); // bind app.tenant_id / app.provider_id GUCs from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "patient-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

// 18.B3 (audit R2 S6) — reads and writes are no longer the same permission.
//
// Everything under /beneficiaries required patient:write, which only beneficiary_mgmt holds. So the desk
// could not look up the person standing in front of it, and whoever COULD look someone up was, by the same
// token, allowed to rewrite their identity record. Reads now take patient:read and go through
// BeneficiaryReadGuard (engine + tenant + PHI-read audit + field projection); writes keep patient:write.
var v1 = app.MapGroup("/api/v1/beneficiaries").RequireAuthorization(HbmpPolicies.Scope("patient:write"));
var read = app.MapGroup("/api/v1/beneficiaries").RequireAuthorization(HbmpPolicies.Scope("patient:read"));

// POST /beneficiaries — register (Idempotency-Key required); 201 + ETag, or 409 duplicate, or 400.
v1.MapPost("", async (
    RegisterRequest body, HttpRequest http,
    BeneficiaryRegistrar registrar, PatientDbContext db, IAuditClient audit, IOutbox outbox,
    IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(http.Headers["Idempotency-Key"]))
        return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");

    var req = body.ToDomain();
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

        // Its own problem type, because its remedy is its own. A duplicate IDENTIFIER means this is the same
        // person and the operator should open them; a duplicate CARD usually means the card was mis-read, or
        // re-issued without the old one being retired — and telling the operator to "open the existing
        // record" would be wrong advice for a genuinely different person holding a recycled card.
        case RegistrationResult.DuplicateCardNumber card:
            return Results.Problem(statusCode: 409, title: "duplicate-card-number",
                detail: $"card number '{card.CardNumber}' is already held by beneficiary {card.ExistingBeneficiaryId}",
                type: "urn:hbmp:duplicate-card-number",
                extensions: new Dictionary<string, object?> { ["existingBeneficiaryId"] = card.ExistingBeneficiaryId });

        case RegistrationResult.Created created:
            db.Beneficiaries.Add(created.Beneficiary);
            // The registration APPLICATION opens with the person, in the same transaction. Until now nothing
            // created these rows — the approval endpoints existed but the worklist behind US-003 was empty
            // unless someone hand-called POST /registrations, which no screen did. A beneficiary without an
            // open application is a person nobody is going to review.
            var registrationId = Guid.NewGuid();
            db.Registrations.Add(new Registration
            {
                RegistrationId = registrationId, BeneficiaryId = created.Beneficiary.BeneficiaryId,
                TenantId = created.Beneficiary.TenantId, Status = RegistrationStatus.Pending,
                // The elected coverage IS the coverage binding US-003 asks the approver to confirm. Recording
                // it as bound here is not skipping the guard: the guard asks whether a policy has been chosen,
                // and one now has been, in a row the approver can read. What remains a supervisor's is the
                // DECISION, which is unchanged.
                CoverageBound = created.Intent is not null,
                CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
            });

            if (created.Intent is { } intent)
            {
                db.EnrolmentIntents.Add(new EnrolmentIntent
                {
                    RegistrationId = registrationId, TenantId = created.Beneficiary.TenantId,
                    PlanId = intent.PlanId, NetworkTierId = intent.NetworkTierId,
                    ContributionPercent = intent.ContributionPercent, DefaultBranchId = intent.DefaultBranchId,
                    CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
                });
            }

            foreach (var note in created.Notes)
            {
                db.RegistrationNotes.Add(new RegistrationNote
                {
                    RegistrationId = registrationId, TenantId = created.Beneficiary.TenantId,
                    Slot = note.Slot, Value = note.Value,
                    // Taken from the SLOT, never from the request. A client that could name the visibility
                    // could file a diagnosis as administrative and route it around the clinical rule.
                    Visibility = RegistrationNoteSlots.VisibilityOf(note.Slot),
                    CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
                });
            }
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary", EntityId = created.Beneficiary.BeneficiaryId.ToString(),
                Action = AuditAction.Create, ActorUserId = actor, FieldClasses = ["identity", "pii"],
            }, ct);
            await outbox.EnqueueAsync("BeneficiaryRegistered", "patient.events",
                new
                {
                    // 18.B2 — the envelope carries its tenant so downstream projections bind RLS from the
                    // event instead of assuming the sole tenant.
                    tenantId = created.Beneficiary.TenantId,
                    beneficiaryId = created.Beneficiary.BeneficiaryId,
                    status = "Pending",
                    cardNumber = created.Beneficiary.CardNumber,
                    givenName = created.Beneficiary.GivenName,
                    middleName = created.Beneficiary.MiddleName,
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
read.MapGet("", async (string? identifierType, string? identifierValue, string? name, string? status,
    int? page, int? pageSize, PatientDbContext db, BeneficiaryReadGuard guard, CancellationToken ct) =>
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
    {
        // TOKENIZED: every whitespace-separated term must match a name column. The natural query is the
        // full name — "Omar Khalil" — and matching the whole string against each column separately meant
        // exactly that query returned nothing (given "Omar" does not contain "Omar Khalil", family
        // "Khalil" does not either), which read as "search is broken" rather than "type less".
        // LIKE metacharacters stay escaped: the value is parameterized (no injection), but an unescaped
        // "%" makes a search for "100%" a full-directory disclosure, each row PHI-read-audited.
        foreach (var term in name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = $"%{term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            q = q.Where(x => EF.Functions.ILike(x.GivenName, pattern, @"\") || EF.Functions.ILike(x.FamilyName, pattern, @"\"));
        }
    }
    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<BeneficiaryStatus>(status, out var st))
        q = q.Where(x => x.Status == st);

    var items = await q.Include(x => x.Identifiers).Include(x => x.Contacts)
        .OrderBy(x => x.FamilyName).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);

    // Every hit is authorized and audited individually. A search is the higher-volume disclosure of the two,
    // and "forty records were returned" is not an audit trail — the row ids are what makes a later review
    // possible. Rows the caller may not read are dropped rather than 403-ing the page, so a name query does
    // not become an oracle for which beneficiaries exist outside the caller's tenant.
    var disclosed = new List<IReadOnlyDictionary<string, object?>>();
    foreach (var b in items)
    {
        if (await guard.AuthorizeAsync(b, ct) is not null) continue;
        disclosed.Add(await guard.DiscloseAsync(b, "search", ct));
    }
    return Results.Ok(new { page = p, pageSize = ps, items = disclosed });
});

// GET /beneficiaries/{id} — engine-authorized, tenant-scoped, audited, field-projected (18.B3 / S6).
read.MapGet("/{id:guid}", async (Guid id, PatientDbContext db, BeneficiaryReadGuard guard, CancellationToken ct) =>
{
    var b = await db.Beneficiaries.AsNoTracking().Include(x => x.Identifiers).Include(x => x.Contacts)
        .FirstOrDefaultAsync(x => x.BeneficiaryId == id && !x.IsDeleted, ct);
    if (b is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
    if (await guard.AuthorizeAsync(b, ct) is { } denied) return denied;
    return Results.Ok(await guard.DiscloseAsync(b, "by-id", ct));
});

// ================================================================ REGISTRATION WORKFLOW (1.4, US-003/004)
var reg = app.MapGroup("/api/v1/registrations").RequireAuthorization(HbmpPolicies.Scope("patient:write"));
var regRead = app.MapGroup("/api/v1/registrations").RequireAuthorization(HbmpPolicies.Scope("patient:read"));

// The approver's WORKLIST (US-003): every Pending beneficiary with its latest application state — including
// the ones with no application row at all (registered before applications were auto-created), because a
// person the queue cannot show is a person nobody reviews. Reads take patient:read; every disclosed row is
// engine-authorized and PHI-read-audited individually, exactly as the directory search is, and rows the
// caller may not read are dropped rather than 403ing the page.
regRead.MapGet("", async (int? page, int? pageSize, PatientDbContext db, BeneficiaryReadGuard guard,
    IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var (p, ps) = (Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 25, 1, 100));

    // Oldest first: this is a queue, and a queue that shows the newest first starves whoever arrived first.
    var pending = await db.Beneficiaries.AsNoTracking()
        .Where(x => !x.IsDeleted && x.Status == BeneficiaryStatus.Pending)
        .Include(x => x.Identifiers).Include(x => x.Contacts)
        .OrderBy(x => x.CreatedAt)
        .Skip((p - 1) * ps).Take(ps)
        .ToListAsync(ct);

    var ids = pending.Select(b => b.BeneficiaryId).ToList();
    var latest = (await db.Registrations.AsNoTracking()
            .Where(r => ids.Contains(r.BeneficiaryId))
            .ToListAsync(ct))
        .GroupBy(r => r.BeneficiaryId)
        .ToDictionary(gr => gr.Key, gr => gr.OrderByDescending(r => r.CreatedAt).First());

    // The elected coverage and the standing notes, fetched once for the page rather than per row.
    var regIds = latest.Values.Select(r => r.RegistrationId).ToList();
    var intents = await db.EnrolmentIntents.AsNoTracking()
        .Where(x => regIds.Contains(x.RegistrationId)).ToDictionaryAsync(x => x.RegistrationId, ct);
    var noteRows = (await db.RegistrationNotes.AsNoTracking()
            .Where(x => regIds.Contains(x.RegistrationId)).ToListAsync(ct))
        .GroupBy(x => x.RegistrationId)
        .ToDictionary(gr => gr.Key, gr => gr.OrderBy(n => n.Slot).ToList());

    // Whether THIS caller may read the clinical slots. The approver is an administrative role, so by default
    // they may not — slot 1 is a diagnosis and slot 3 a treatment, and 18-security-model.md does not make an
    // exception for the fact that an administrator typed them in. Capture is not disclosure.
    var mayReadClinical = NoteProjection.MayReadClinical(me.Principal?.Roles);

    var items = new List<object>();
    foreach (var b in pending)
    {
        if (await guard.AuthorizeAsync(b, ct) is not null) continue;
        var disclosed = await guard.DiscloseAsync(b, "approval-worklist", ct);
        latest.TryGetValue(b.BeneficiaryId, out var r);
        items.Add(new
        {
            beneficiary = disclosed,
            registration = r is null ? null : new
            {
                r.RegistrationId,
                status = r.Status.ToString(),
                r.DocumentsVerified,
                r.CoverageBound,
                r.Notes,
                r.UpdatedAt,
                // What the supervisor is actually approving. Deciding on a registration without being shown
                // the plan, the tier and the member's share is deciding on a name and a checkbox.
                enrolment = intents.TryGetValue(r.RegistrationId, out var intent)
                    ? new
                    {
                        intent.PlanId, intent.NetworkTierId,
                        intent.ContributionPercent, intent.DefaultBranchId,
                    }
                    : null,
                standingNotes = NoteProjection.Project(
                    noteRows.GetValueOrDefault(r.RegistrationId, []), mayReadClinical),
            },
        });
    }
    return Results.Ok(new { page = p, pageSize = ps, items });
});

// Create a registration for an existing (Pending) beneficiary — the re-review path (a Rejected application
// is final, so a fresh look is a fresh row) and the backfill for beneficiaries registered before
// applications were auto-created.
reg.MapPost("", async (CreateRegistration req, HttpRequest http, PatientDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(http.Headers["Idempotency-Key"]))
        return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required");
    var beneficiary = await db.Beneficiaries.AsNoTracking()
        .FirstOrDefaultAsync(x => x.BeneficiaryId == req.BeneficiaryId && !x.IsDeleted, ct);
    if (beneficiary is null)
        return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
    var now = clock.GetUtcNow();
    // TenantId comes from the beneficiary. It used to be left at its CLR default (""), which the FORCED RLS
    // policy on patient.registration refuses to insert under the NOBYPASSRLS runtime role — the endpoint
    // only ever worked in tests, which connect as the table owner and bypass the policy entirely.
    var r = new Registration { RegistrationId = Guid.NewGuid(), BeneficiaryId = req.BeneficiaryId, TenantId = beneficiary.TenantId, Status = RegistrationStatus.Pending, CreatedAt = now, UpdatedAt = now };
    db.Registrations.Add(r);
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "registration", EntityId = r.RegistrationId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject }, ct);
    return Results.Created($"/api/v1/registrations/{r.RegistrationId}", new { r.RegistrationId, status = r.Status.ToString() });
});

// Set step data (documents verified / coverage bound / notes).
reg.MapPatch("/{id:guid}", async (Guid id, PatchRegistration req, PatientDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
{
    var r = await db.Registrations.FirstOrDefaultAsync(x => x.RegistrationId == id, ct);
    if (r is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
    var before = $"{{\"documentsVerified\":{r.DocumentsVerified.ToString().ToLowerInvariant()},\"coverageBound\":{r.CoverageBound.ToString().ToLowerInvariant()}}}";
    if (req.DocumentsVerified is { } dv) r.DocumentsVerified = dv;
    if (req.CoverageBound is { } cb) r.CoverageBound = cb;
    if (req.Notes is not null) r.Notes = req.Notes;
    r.UpdatedAt = clock.GetUtcNow();
    await db.SaveChangesAsync(ct);
    var after = $"{{\"documentsVerified\":{r.DocumentsVerified.ToString().ToLowerInvariant()},\"coverageBound\":{r.CoverageBound.ToString().ToLowerInvariant()}}}";
    await audit.EmitAsync(new AuditEventDraft { EntityType = "registration", EntityId = r.RegistrationId.ToString(), Action = AuditAction.Update, ActorUserId = me.Principal?.Subject, BeforeState = before, AfterState = after }, ct);
    return Results.Ok(new { r.RegistrationId, status = r.Status.ToString(), r.DocumentsVerified, r.CoverageBound });
});

// Decision: Approve → activate (issue Member No + BeneficiaryActivated); RequestInfo/Reject (reason mandatory).
reg.MapPost("/{id:guid}/decision", async (Guid id, DecisionRequest req, PatientDbContext db, MemberNoIssuer memberNos, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
{
    if (!Enum.TryParse<RegistrationDecision>(req.Decision, ignoreCase: true, out var decision))
        return Results.Problem(statusCode: 400, title: $"invalid decision '{req.Decision}'");

    // US-003 names the APPROVER, and the separation is the point: the officer who registered a person and
    // vouched for their documents must not be the one who activates them. patient:write alone would let
    // exactly that happen — both roles hold it, because the officer needs it for everything else. The
    // denial is audited: a refused self-approval is evidence, not noise.
    if (me.Principal is { } who && !who.IsInRole("beneficiary_mgmt_supervisor"))
    {
        await audit.EmitAsync(new AuditEventDraft { EntityType = "registration", EntityId = id.ToString(), Action = AuditAction.Decision, ActorUserId = who.Subject, DecisionOutcome = "DecisionDenied", DecisionReasonCode = "approver role required (US-003)" }, ct);
        return Results.Problem(statusCode: 403, title: "approver-required",
            detail: "registration decisions are made by a beneficiary-management supervisor (US-003)",
            type: "urn:hbmp:approver-required");
    }

    var r = await db.Registrations.FirstOrDefaultAsync(x => x.RegistrationId == id, ct);
    if (r is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    var error = RegistrationRules.ValidateDecision(r, decision, req.Notes);
    if (error is not null) return Results.Problem(statusCode: 422, title: "decision-rejected", detail: error);

    var beneficiary = await db.Beneficiaries.FirstAsync(x => x.BeneficiaryId == r.BeneficiaryId, ct);
    var actor = me.Principal?.Subject;
    r.Status = RegistrationRules.ResultOf(decision);
    r.Notes = req.Notes;
    r.UpdatedAt = clock.GetUtcNow();

    if (decision == RegistrationDecision.Approve)
    {
        // Transactional activation: beneficiary Active + Member No + MemberNo identifier + BeneficiaryActivated.
        // 18.A3 — member numbers are stamped with the Cairo year, not the UTC year.
        var memberNo = await memberNos.NextAsync(calendar.Today().Year, ct);
        beneficiary.Status = BeneficiaryStatus.Active;
        beneficiary.MemberNo = memberNo;
        beneficiary.UpdatedBy = actor;
        beneficiary.UpdatedAt = clock.GetUtcNow();
        db.Identifiers.Add(new BeneficiaryIdentifier
        {
            IdentifierId = Guid.NewGuid(), BeneficiaryId = beneficiary.BeneficiaryId,
            IdentifierType = IdentifierType.MemberNo, IdentifierValue = memberNo, IsPrimary = false,
        });
        await db.SaveChangesAsync(ct);
        await audit.EmitAsync(new AuditEventDraft { EntityType = "beneficiary", EntityId = beneficiary.BeneficiaryId.ToString(), Action = AuditAction.StateChange, ActorUserId = actor, DecisionOutcome = "Activated", AfterState = $"{{\"memberNo\":\"{memberNo}\"}}" }, ct);
        await outbox.EnqueueAsync("BeneficiaryActivated", "patient.events", new { tenantId = beneficiary.TenantId, beneficiaryId = beneficiary.BeneficiaryId, memberNo, givenName = beneficiary.GivenName, familyName = beneficiary.FamilyName }, ct);

        // THE INTENT BECOMES A MEMBERSHIP. Approval is the moment the coverage elected at the desk stops
        // being a plan and starts being one — which is what `coverage_bound` has always claimed and never
        // did. Published through the same outbox, in the same transaction as the activation, so a broker
        // that is down delays the enrolment rather than losing it; policy-service's consumer is idempotent
        // on the event id, so a redelivery does not enrol the person twice.
        //
        // Absent for a legacy registration opened before intents existed. That is not an error — those are
        // enrolled by hand on the Members screen, exactly as they are today.
        var enrolment = await db.EnrolmentIntents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.RegistrationId == r.RegistrationId, ct);
        if (enrolment is not null)
        {
            // A DEDICATED destination, not the shared `patient.events` stream. The transport publishes
            // point-to-point (default exchange, routing key = queue name), so every consumer attached to one
            // queue COMPETES for its messages: adding policy-service to patient.events would have had
            // RabbitMQ round-robin each event between it and eligibility-service, and eligibility acks event
            // types it does not handle. The result would be an enrolment that happens for roughly half the
            // approvals and silently vanishes for the rest — the worst possible failure here, because the
            // member number is issued either way and nothing looks wrong.
            await outbox.EnqueueAsync("RegistrationEnrolmentRequested", "policy.registration-enrolments", new
            {
                tenantId = beneficiary.TenantId,
                registrationId = r.RegistrationId,
                beneficiaryId = beneficiary.BeneficiaryId,
                memberNo,
                planId = enrolment.PlanId,
                networkTierId = enrolment.NetworkTierId,
                contributionPercent = enrolment.ContributionPercent,
                branchId = enrolment.DefaultBranchId,
                // Cover starts the day the supervisor approved, not the day the form was filled in. A
                // back-dated start would grant benefit for the review window, which nobody decided to grant.
                effectiveFrom = calendar.Today().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }, ct);
        }
        return Results.Ok(new { r.RegistrationId, status = r.Status.ToString(), beneficiary.BeneficiaryId, memberNo });
    }

    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "registration", EntityId = r.RegistrationId.ToString(), Action = AuditAction.Decision, ActorUserId = actor, DecisionOutcome = r.Status.ToString(), DecisionReasonCode = req.Notes }, ct);
    return Results.Ok(new { r.RegistrationId, status = r.Status.ToString(), r.Notes });
});

// ================================================================ LIFECYCLE TRANSITIONS (US-004)
v1.MapPost("/{id:guid}/status", async (Guid id, StatusChange req, PatientDbContext db, MemberNoIssuer memberNos, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
{
    if (!Enum.TryParse<BeneficiaryStatus>(req.ToStatus, ignoreCase: true, out var to))
        return Results.Problem(statusCode: 400, title: $"invalid status '{req.ToStatus}'");
    var b = await db.Beneficiaries.FirstOrDefaultAsync(x => x.BeneficiaryId == id && !x.IsDeleted, ct);
    if (b is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    var error = BeneficiaryLifecycle.Validate(b.Status, to, req.Reason);
    if (error is not null)
    {
        await audit.EmitAsync(new AuditEventDraft { EntityType = "beneficiary", EntityId = id.ToString(), Action = AuditAction.StateChange, ActorUserId = me.Principal?.Subject, DecisionOutcome = "TransitionDenied", DecisionReasonCode = error }, ct);
        return Results.Problem(statusCode: 409, title: "transition-denied", detail: error);
    }

    // 23 §1's Actor column for the FRAUD edges. Blocking and unblocking were reachable by any patient:write
    // holder, so a fraud block could be quietly lifted at the desk — the same erasure 18.A4 closed the
    // Blocked→Inactive edge against. Denied attempts are audited: a refused unblock is exactly the event a
    // fraud review wants to see.
    if (BeneficiaryLifecycle.DeniedActor(b.Status, to, me.Principal?.Roles ?? new HashSet<string>()) is { } requiredRoles)
    {
        await audit.EmitAsync(new AuditEventDraft { EntityType = "beneficiary", EntityId = id.ToString(), Action = AuditAction.StateChange, ActorUserId = me.Principal?.Subject, DecisionOutcome = "TransitionDenied", DecisionReasonCode = $"{b.Status}->{to} requires {requiredRoles}" }, ct);
        return Results.Problem(statusCode: 403, title: "transition-actor-denied",
            detail: $"moving a beneficiary {b.Status} → {to} requires {requiredRoles} (23 §1)",
            type: "urn:hbmp:transition-actor-denied");
    }

    var from = b.Status;
    b.Status = to; b.UpdatedBy = me.Principal?.Subject; b.UpdatedAt = clock.GetUtcNow();

    // First activation issues the member number, whichever door it came through. The registration-decision
    // path below has always done this; this path (Pending→Active at the desk, Inactive→Active reactivation
    // of a never-carded record) previously produced an ACTIVE member with no MRS-M number — nothing
    // downstream could reference them, and no BeneficiaryActivated event ever told eligibility they exist.
    // Renewal/unblock of an already-carded member keeps its existing number: the number is an identity fact,
    // not a status fact.
    string? issuedMemberNo = null;
    if (to == BeneficiaryStatus.Active && string.IsNullOrEmpty(b.MemberNo))
    {
        issuedMemberNo = await memberNos.NextAsync(calendar.Today().Year, ct);
        b.MemberNo = issuedMemberNo;
        db.Identifiers.Add(new BeneficiaryIdentifier
        {
            IdentifierId = Guid.NewGuid(), BeneficiaryId = b.BeneficiaryId,
            IdentifierType = IdentifierType.MemberNo, IdentifierValue = issuedMemberNo, IsPrimary = false,
        });
    }

    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "beneficiary", EntityId = id.ToString(), Action = AuditAction.StateChange, ActorUserId = me.Principal?.Subject, BeforeState = $"{{\"status\":\"{from}\"}}", AfterState = issuedMemberNo is null ? $"{{\"status\":\"{to}\"}}" : $"{{\"status\":\"{to}\",\"memberNo\":\"{issuedMemberNo}\"}}", DecisionReasonCode = req.Reason }, ct);
    await outbox.EnqueueAsync("BeneficiaryStatusChanged", "patient.events", new { tenantId = b.TenantId, beneficiaryId = id, from = from.ToString(), to = to.ToString(), reason = req.Reason }, ct);
    if (issuedMemberNo is not null)
        await outbox.EnqueueAsync("BeneficiaryActivated", "patient.events", new { tenantId = b.TenantId, beneficiaryId = b.BeneficiaryId, memberNo = issuedMemberNo, givenName = b.GivenName, familyName = b.FamilyName }, ct);

    return Results.Ok(new { beneficiaryId = id, from = from.ToString(), to = to.ToString(), status = to.ToString(), memberNo = b.MemberNo });
}).RequireAuthorization(HbmpPolicies.Scope("patient:write"));

app.MapBeneficiarySummaries();   // 19.5 — name-only batch for one page of policy-service's member query
app.MapBeneficiaryContacts();    // 19.5b — read/upsert contacts; the owner of the field owns the write path

// Register-or-update by card number — what makes a corrected intake file safe to re-upload.
app.MapBeneficiaryIntake();

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
