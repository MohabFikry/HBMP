using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Patient.Api;
using Mersal.Patient.Domain;
using Mersal.Patient.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("patient-service");
builder.Services.AddHbmpAuthorization();
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddPatientInfrastructure(builder.Configuration);
builder.Services.AddScoped<BeneficiaryRegistrar>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("patient-service"))
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
                new { beneficiaryId = created.Beneficiary.BeneficiaryId, status = "Pending" }, ct);

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

app.Run();

public partial class Program;
