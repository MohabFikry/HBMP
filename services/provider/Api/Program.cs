using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Provider.Api;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProviderEntity = Mersal.Provider.Domain.Provider;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("provider-service");
// Provider-service authorizes with the platform bundle plus the provider-ownership rules (2b.3).
builder.Services.AddHbmpAuthorization(ProviderPolicies.Bundle());
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddProviderInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ProviderAccessGuard>();
builder.Services.AddSingleton(TimeProvider.System);

// masterdata-backed code validation for CPT/LOINC service-line codes.
builder.Services.AddHttpClient<ICodeValidator, HttpCodeValidator>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Masterdata:BaseUrl"] ?? "http://masterdata-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("provider-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter());
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Provider isolation, layers 1 & 4: reject provider-scoped tokens with no provider_id on provider routes,
// and bind the RLS session GUCs (tenant_id / provider_id) for this request's DB connections.
app.Use(async (ctx, next) =>
{
    var principal = ctx.RequestServices.GetRequiredService<IHbmpPrincipalAccessor>().Principal;
    if (principal is not null && ctx.Request.Path.StartsWithSegments("/api/v1"))
    {
        if (ProviderAccessGuard.TokenMissingProviderId(principal))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { title = "provider-scoped token is missing a provider_id claim" });
            return;
        }
        var rls = ctx.RequestServices.GetRequiredService<Mersal.Provider.Infrastructure.RlsContext>();
        rls.TenantId = principal.TenantId ?? "";
        rls.ProviderId = principal.ProviderId ?? "";
    }
    await next();
});

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "provider-service" })).AllowAnonymous();

// ------------------------------------------------------------------ helpers
static string? Bearer(HttpContext http) => http.Request.Headers.Authorization.FirstOrDefault();
static ProviderView ToView(ProviderEntity p) => new(
    p.ProviderId, p.ProviderCode, p.LegalName, p.ProviderType.ToString(),
    ProviderTypeLabels.Label(p.ProviderType), p.Status.ToString(), p.OnboardingState.ToString());

// Network Team writes (provider:write); Provider Admin / Network Team read (provider:read).
var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:write"));
var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:read"));

// --- Create provider (Draft) → ProviderCreated -------------------------------------------------
write.MapPost("/providers", async (CreateProvider req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");
    if (!Enum.TryParse<ProviderType>(req.ProviderType, out var type))
        return Results.Problem(statusCode: 400, title: $"unknown provider_type '{req.ProviderType}'");

    var now = DateTimeOffset.UtcNow;
    var p = new ProviderEntity
    {
        ProviderId = Guid.NewGuid(), TenantId = tenant, ProviderCode = req.ProviderCode, LegalName = req.LegalName,
        ProviderType = type, Status = ProviderStatus.Suspended, OnboardingState = OnboardingState.Draft,
        CreatedAt = now, UpdatedAt = now,
    };
    db.Providers.Add(p);
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "provider", EntityId = p.ProviderId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject, TenantId = tenant }, ct);
    await outbox.EnqueueAsync("ProviderCreated", "provider.events", new { providerId = p.ProviderId, p.ProviderCode, providerType = p.ProviderType.ToString(), tenantId = tenant }, ct);
    return Results.Created($"/api/v1/providers/{p.ProviderId}", ToView(p));
});

// --- List / get (tenant-scoped) ----------------------------------------------------------------
read.MapGet("/providers", async (ProviderDbContext db, IHbmpPrincipalAccessor me, string? status, CancellationToken ct) =>
{
    var principal = me.Principal;
    var tenant = principal?.TenantId;
    var q = db.Providers.AsNoTracking().Where(p => p.TenantId == tenant && !p.IsDeleted);
    // Provider-scoped callers list ONLY their own provider (ABAC PO on a collection; RLS also enforces it).
    if (principal is not null && ProviderAccessGuard.IsProviderScoped(principal))
        q = q.Where(p => p.ProviderId.ToString() == principal.ProviderId);
    if (status is not null && Enum.TryParse<ProviderStatus>(status, out var s)) q = q.Where(p => p.Status == s);
    return Results.Ok((await q.ToListAsync(ct)).Select(ToView));
});

read.MapGet("/providers/{id:guid}", async (Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.AsNoTracking().FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.NotFound();
    // ABAC provider-ownership: a provider user reading another provider is denied AND audited.
    var decision = await guard.AuthorizeAsync(me.Require(), p.TenantId, p.ProviderId.ToString(), ct);
    if (!decision.IsAllowed) return Results.Problem(statusCode: 403, title: "provider access denied", detail: decision.ReasonCode);
    return Results.Ok(ToView(p));
});

// --- Add location (primary rule enforced by partial-unique index) ------------------------------
write.MapPost("/providers/{id:guid}/locations", async (Guid id, CreateLocation req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.NotFound();

    var loc = new ProviderLocation
    {
        LocationId = Guid.NewGuid(), ProviderId = id, TenantId = tenant!, Name = req.Name,
        Governorate = req.Governorate, Address = req.Address, GeoLat = req.GeoLat, GeoLng = req.GeoLng,
        IsPrimary = req.IsPrimary,
    };
    db.Locations.Add(loc);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "provider already has a primary location"); }
    await audit.EmitAsync(new AuditEventDraft { EntityType = "provider_location", EntityId = loc.LocationId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject, TenantId = tenant }, ct);
    return Results.Created($"/api/v1/providers/{id}/locations/{loc.LocationId}", new { loc.LocationId, loc.IsPrimary });
});

// --- Add contract (effective-range overlap rejected by exclusion constraint) -------------------
write.MapPost("/providers/{id:guid}/contracts", async (Guid id, CreateContract req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.Include(x => x.Contracts).FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.NotFound();
    if (ContractRules.OverlapsAny(p.Contracts, req.EffectiveFrom, req.EffectiveTo))
        return Results.Problem(statusCode: 409, title: "contract effective range overlaps an existing contract");

    var c = new ProviderContract
    {
        ContractId = Guid.NewGuid(), ProviderId = id, TenantId = tenant!, ContractNo = req.ContractNo,
        EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo, Status = ContractStatus.Draft,
    };
    db.Contracts.Add(c);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "contract overlaps or contract_no already exists"); }
    await audit.EmitAsync(new AuditEventDraft { EntityType = "provider_contract", EntityId = c.ContractId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject, TenantId = tenant }, ct);
    return Results.Created($"/api/v1/contracts/{c.ContractId}", new { c.ContractId, c.ContractNo, status = c.Status.ToString() });
});

// --- Add service line (masterdata validation for CPT/LOINC) ------------------------------------
write.MapPost("/contracts/{contractId:guid}/service-lines", async (Guid contractId, AddServiceLine req, ProviderDbContext db, ICodeValidator codes, IAuditClient audit, IHbmpPrincipalAccessor me, HttpContext http, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var c = await db.Contracts.FirstOrDefaultAsync(x => x.ContractId == contractId && x.TenantId == tenant && !x.IsDeleted, ct);
    if (c is null) return Results.NotFound();
    if (!Enum.TryParse<ServiceType>(req.ServiceType, out var st)) return Results.Problem(statusCode: 400, title: $"unknown service_type '{req.ServiceType}'");
    if (!Enum.TryParse<CodeSystem>(req.CodeSystem, out var cs)) return Results.Problem(statusCode: 400, title: $"unknown code_system '{req.CodeSystem}'");
    if (req.AgreedPrice < 0) return Results.Problem(statusCode: 400, title: "agreed_price must be >= 0");
    if (!await codes.IsValidAsync(cs, req.Code, Bearer(http), ct))
        return Results.Problem(statusCode: 400, title: $"{cs} code '{req.Code}' not found in master data");

    var line = new ContractServiceLine
    {
        ServiceLineId = Guid.NewGuid(), ContractId = contractId, TenantId = tenant!, ServiceType = st,
        CodeSystem = cs, Code = req.Code, AgreedPrice = req.AgreedPrice, CurrencyCode = req.CurrencyCode ?? "EGP",
    };
    db.ServiceLines.Add(line);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "service line for this code already exists on the contract"); }
    await audit.EmitAsync(new AuditEventDraft { EntityType = "contract_service_line", EntityId = line.ServiceLineId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject, TenantId = tenant, FieldClasses = ["financials"] }, ct);
    return Results.Created($"/api/v1/service-lines/{line.ServiceLineId}", new { line.ServiceLineId, code = line.Code });
});

// --- Activate contract → ContractActivated -----------------------------------------------------
write.MapPost("/contracts/{contractId:guid}/activate", async (Guid contractId, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var c = await db.Contracts.Include(x => x.ServiceLines).FirstOrDefaultAsync(x => x.ContractId == contractId && x.TenantId == tenant && !x.IsDeleted, ct);
    if (c is null) return Results.NotFound();
    if (c.ServiceLines.Count == 0) return Results.Problem(statusCode: 422, title: "cannot activate a contract with no service lines");
    c.Status = ContractStatus.Active;
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "provider_contract", EntityId = c.ContractId.ToString(), Action = AuditAction.StateChange, DecisionOutcome = "Active", ActorUserId = me.Principal?.Subject, TenantId = tenant }, ct);
    await outbox.EnqueueAsync("ContractActivated", "provider.events", new { contractId = c.ContractId, providerId = c.ProviderId, c.ContractNo, tenantId = tenant }, ct);
    return Results.Ok(new { c.ContractId, status = c.Status.ToString() });
});

// --- Add credential ----------------------------------------------------------------------------
write.MapPost("/providers/{id:guid}/credentials", async (Guid id, AddCredential req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.NotFound();
    var cred = new ProviderCredential
    {
        CredentialId = Guid.NewGuid(), ProviderId = id, TenantId = tenant!, CredentialType = req.CredentialType,
        Status = req.DocumentId is null ? CredentialStatus.Pending : CredentialStatus.Valid,
        ValidFrom = req.ValidFrom, ValidTo = req.ValidTo, DocumentId = req.DocumentId, IsMandatory = req.IsMandatory,
    };
    db.Credentials.Add(cred);
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "provider_credential", EntityId = cred.CredentialId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject, TenantId = tenant }, ct);
    return Results.Created($"/api/v1/providers/{id}/credentials/{cred.CredentialId}", new { cred.CredentialId, status = cred.Status.ToString() });
});

// --- Capabilities (routable codes; agreed_price masked without provider:finance) ---------------
read.MapGet("/providers/{id:guid}/capabilities", async (Guid id, ProviderDbContext db, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.AsNoTracking()
        .Include(x => x.Contracts).ThenInclude(c => c.ServiceLines)
        .FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.NotFound();

    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    var canSeePrice = me.Principal?.HasScope("provider:finance") ?? false;
    // Join derived capabilities back to their priced service lines for the (masked) price view.
    var lines = p.Contracts.Where(c => ContractRules.InEffect(c, today)).SelectMany(c => c.ServiceLines);
    var caps = CapabilityDerivation.Derive(p, today).Select(cap =>
    {
        var line = lines.FirstOrDefault(l => l.CodeSystem == cap.CodeSystem && l.Code == cap.Code);
        return new CapabilityView(cap.ServiceType.ToString(), cap.CodeSystem.ToString(), cap.Code,
            canSeePrice ? line?.AgreedPrice : null, canSeePrice ? line?.CurrencyCode : null);
    });
    return Results.Ok(new { p.ProviderId, status = p.Status.ToString(), routable = p.Status == ProviderStatus.Active, capabilities = caps });
});

// --- Read a provider's locations (tenant + ABAC PO gated) --------------------------------------
read.MapGet("/providers/{id:guid}/locations", async (Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.AsNoTracking().FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.NotFound();
    var decision = await guard.AuthorizeAsync(me.Require(), p.TenantId, p.ProviderId.ToString(), ct);
    if (!decision.IsAllowed) return Results.Problem(statusCode: 403, title: "provider access denied", detail: decision.ReasonCode);
    var rows = await db.Locations.AsNoTracking().Where(l => l.ProviderId == id && !l.IsDeleted).OrderByDescending(l => l.IsPrimary).ToListAsync(ct);
    return Results.Ok(rows.Select(l => new { l.LocationId, l.Name, l.Governorate, l.Address, l.IsPrimary }));
});

// --- Read a provider's contracts + service-line counts (tenant + ABAC PO gated) ----------------
read.MapGet("/providers/{id:guid}/contracts", async (Guid id, ProviderDbContext db, ProviderAccessGuard guard, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.AsNoTracking().FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.NotFound();
    var decision = await guard.AuthorizeAsync(me.Require(), p.TenantId, p.ProviderId.ToString(), ct);
    if (!decision.IsAllowed) return Results.Problem(statusCode: 403, title: "provider access denied", detail: decision.ReasonCode);
    var rows = await db.Contracts.AsNoTracking().Include(c => c.ServiceLines).Where(c => c.ProviderId == id && !c.IsDeleted).OrderByDescending(c => c.EffectiveFrom).ToListAsync(ct);
    return Results.Ok(rows.Select(c => new { c.ContractId, c.ContractNo, status = c.Status.ToString(), c.EffectiveFrom, c.EffectiveTo, serviceLines = c.ServiceLines.Count }));
});

// 2b.2 — Network Team onboarding workflow (activate/suspend/terminate, user provisioning, reminders).
app.MapOnboarding();
// 2b.3 — provider-scoped performance metrics + network roll-up.
app.MapMetrics();
// 14.1 — internal Mersal branch registry (org reference data; reads open, writes Network/Org Admin).
app.MapBranches();
// 14.5 — practitioners, specialty & doctor↔branch assignment + the doctor picker + serves-branch probe.
app.MapPractitioners();

app.Run();

public partial class Program;
