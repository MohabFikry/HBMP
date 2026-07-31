using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Data;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Provider.Api;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Mersal.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ProviderEntity = Mersal.Provider.Domain.Provider;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpBusinessCalendar();   // 18.A3 — Africa/Cairo business dates + injected clock
builder.Services.AddHbmpAuditClient("provider-service");
// Provider-service authorizes with the platform bundle plus the provider-ownership rules (2b.3).
builder.Services.AddHbmpAuthorization(ProviderPolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<ProviderDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddProviderInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ProviderAccessGuard>();
builder.Services.AddScoped<NetworkTierGate>();   // 19.1b — Network-Team-only tier administration
// 19.1b — the guard on CORRECTING a tier assignment. Default reports zero (see UnwiredAdjudicatedClaimProbe):
// a known open gap awaiting the claims read-model query, not a safe default.
builder.Services.AddScoped<IAdjudicatedClaimProbe, UnwiredAdjudicatedClaimProbe>();
builder.Services.AddSingleton(TimeProvider.System);

// 25.2 (design 42 §2) — active-branch context. provider-service never needed it before: every write here was
// network-wide (provider:write) and the branch dimension narrowed nobody. `branch:practitioner:write` changes
// that — a coordinator's authority is sized to their clinic, so the service now has to KNOW which clinic that
// is. The permitted set is resolved per request from admin-service and read by BranchReachGuard.
builder.Services.AddScoped<BranchScopeState>();
builder.Services.AddScoped<BranchReachGuard>();
builder.Services.AddHttpClient<IBranchDirectory, HttpBranchDirectory>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Admin:BaseUrl"] ?? "http://admin-service:8080"));

// 25.3 (design 42 §3) — warn 90/60/30 days before a licence lapses and announce it on the day. Nothing
// happens when a licence expires — no request, no button, the date simply passes — so the only way a lapse
// becomes visible is if something goes looking. Mirrors orders' ReportAccessExpirySweeper.
builder.Services.AddHostedService<PractitionerLicenceExpirySweeper>();

// masterdata-backed code validation for CPT/LOINC service-line codes.
builder.Services.AddHttpClient<ICodeValidator, HttpCodeValidator>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Masterdata:BaseUrl"] ?? "http://masterdata-service:8080"));

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("provider-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());
builder.Services.AddProblemDetails();
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

// Provider isolation, layer 1: reject provider-scoped tokens with no provider_id on provider routes.
// 18.E2 — this used to ALSO hand-roll the RLS binding, duplicating UseHbmpRls line for line. A bespoke copy
// of shared wiring is exactly what drifts: the shared helper gained behaviour over three phases and this
// copy did not, and the architecture test could not tell "binds the GUC" from "does not" by reading it.
// The guard stays here (it is provider-specific); the binding is the shared call below.
app.Use(async (ctx, next) =>
{
    var principal = ctx.RequestServices.GetRequiredService<IHbmpPrincipalAccessor>().Principal;
    if (principal is not null && ctx.Request.Path.StartsWithSegments("/api/v1")
        && ProviderAccessGuard.TokenMissingProviderId(principal))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { title = "provider-scoped token is missing a provider_id claim" });
        return;
    }
    await next();
});

// Bind app.tenant_id / app.provider_id from the principal (RLS, ADR-0011) — the shared binder every other
// service uses, so provider inherits the same behaviour rather than a snapshot of it.
app.UseHbmpRls();

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

// 25.2 — resolve the active-branch context per request (design 37 §3, 42 §1). Mirrors emr's middleware:
// BranchScoped callers are narrowed to a validated active branch, BranchSetScoped callers carry their whole
// permitted set with the header acting as a filter, and an X-Active-Branch outside the permitted set is
// refused 403 + audited. THE INVARIANT: never trust the header — always resolve it against the grants.
//
// Member/provider-scoped callers (the Network Team on provider:write) are branch-unrestricted, so this adds
// no narrowing to any pre-25.2 caller.
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
            var branchAudit = ctx.RequestServices.GetRequiredService<IAuditClient>();
            await branchAudit.EmitAsync(new AuditEventDraft
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
        if (state.Context.ActiveBranchId is { } activeBranch) ctx.Response.Headers["X-Active-Branch"] = activeBranch.ToString();
    }
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "provider-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

// ------------------------------------------------------------------ helpers
static string? Bearer(HttpContext http) => http.Request.Headers.Authorization.FirstOrDefault();
static ProviderView ToView(ProviderEntity p) => new(
    p.ProviderId, p.ProviderCode, p.LegalName, p.ProviderType.ToString(),
    ProviderTypeLabels.Label(p.ProviderType), p.Status.ToString(), p.OnboardingState.ToString());

// Network Team writes (provider:write); Provider Admin / Network Team read (provider:read).
var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:write"));
var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:read"));

/*
 * --- Clinic LABELS for schedulers (appointment:read) ------------------------------------------
 *
 * Reception has to name a clinic to book an appointment, and every read above needs provider:read — which
 * the front desk correctly does not have: the provider DIRECTORY is contracts, onboarding state, capabilities
 * and the shape of the network, none of which is reception's business. Refusing the whole directory was right;
 * the consequence was that booking could not label a clinic at all.
 *
 * So this returns LABELS AND NOTHING ELSE — a name for an id the caller already holds. No contract, no status,
 * no onboarding state, no address, no capability. It is gated on appointment:read (the scheduling scope) and
 * requires explicit ids, so it cannot be used to enumerate the network: a caller can only put a name to
 * locations it already learned about from the slots it is allowed to see.
 */
var labels = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("appointment:read"));
/*
 * Branch LABELS, same shape and same reasoning as clinic-labels below: names for ids the caller already holds,
 * and nothing else. The cross-branch appointment boards show which branch each appointment belongs to, and the
 * only branch names on the platform live here behind provider:read — which the call centre and the desks do not
 * have, and should not, since the branch DIRECTORY is network administration.
 */
labels.MapGet("/branch-labels", async (string? branchIds, ProviderDbContext db, CancellationToken ct) =>
{
    var ids = (branchIds ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => Guid.TryParse(x, out var g) ? g : (Guid?)null)
        .Where(g => g is not null).Select(g => g!.Value).Distinct().Take(200).ToList();
    // No ids ⇒ empty, never the whole branch list.
    if (ids.Count == 0) return Results.Ok(Array.Empty<object>());

    var rows = await db.Branches.AsNoTracking()
        .Where(b => ids.Contains(b.BranchId) && !b.IsDeleted)
        .Select(b => new { b.BranchId, nameEn = b.NameEn, nameAr = b.NameAr })
        .ToListAsync(ct);
    return Results.Ok(rows);
});

labels.MapGet("/clinic-labels", async (string? locationIds, ProviderDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var ids = (locationIds ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
        .Where(g => g is not null).Select(g => g!.Value).Distinct().Take(200).ToList();
    // No ids ⇒ empty, never "everything": this endpoint must not become a directory listing by omission.
    if (ids.Count == 0) return Results.Ok(Array.Empty<object>());

    var rows = await db.Locations.AsNoTracking()
        .Where(l => ids.Contains(l.LocationId) && l.TenantId == tenant && !l.IsDeleted)
        .Join(db.Providers.AsNoTracking().Where(p => p.TenantId == tenant && !p.IsDeleted),
              l => l.ProviderId, p => p.ProviderId,
              (l, p) => new { l.LocationId, l.ProviderId, LocationName = l.Name, p.LegalName })
        .ToListAsync(ct);

    return Results.Ok(rows.Select(r => new
    {
        r.LocationId,
        r.ProviderId,
        locationName = r.LocationName,
        providerName = r.LegalName,
    }));
});

// --- Create provider (Draft) → ProviderCreated -------------------------------------------------
write.MapPost("/providers", async (CreateProvider req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");
    if (!Enum.TryParse<ProviderType>(req.ProviderType, out var type))
        return Results.Problem(statusCode: 400, title: $"unknown provider_type '{req.ProviderType}'");

    var now = clock.GetUtcNow();
    var p = new ProviderEntity
    {
        ProviderId = Guid.NewGuid(), TenantId = tenant, ProviderCode = req.ProviderCode, LegalName = req.LegalName,
        ProviderType = type, Status = ProviderStatus.Suspended, OnboardingState = OnboardingState.Draft,
        CreatedAt = now, UpdatedAt = now,
    };
    // 24.3 — the provider row and ProviderCreated commit together.
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    db.Providers.Add(p);
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "provider", EntityId = p.ProviderId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject, TenantId = tenant }, ct);
    await outbox.EnqueueAsync("ProviderCreated", "provider.events", new { providerId = p.ProviderId, p.ProviderCode, providerType = p.ProviderType.ToString(), tenantId = tenant }, ct);
    await tx.CommitAsync(ct);
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
    if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
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
    if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

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
    if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
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
    if (c is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
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
    if (c is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
    if (c.ServiceLines.Count == 0) return Results.Problem(statusCode: 422, title: "cannot activate a contract with no service lines");
    // 24.3 — an active contract whose ContractActivated event was lost is a tariff nothing downstream
    // prices against: claims adjudicate at the wrong rate and nobody sees why.
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    c.Status = ContractStatus.Active;
    await db.SaveChangesAsync(ct);
    await audit.EmitAsync(new AuditEventDraft { EntityType = "provider_contract", EntityId = c.ContractId.ToString(), Action = AuditAction.StateChange, DecisionOutcome = "Active", ActorUserId = me.Principal?.Subject, TenantId = tenant }, ct);
    await outbox.EnqueueAsync("ContractActivated", "provider.events", new { contractId = c.ContractId, providerId = c.ProviderId, c.ContractNo, tenantId = tenant }, ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { c.ContractId, status = c.Status.ToString() });
});

// --- Add credential ----------------------------------------------------------------------------
write.MapPost("/providers/{id:guid}/credentials", async (Guid id, AddCredential req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
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
read.MapGet("/providers/{id:guid}/capabilities", async (Guid id, ProviderDbContext db, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
{
    var tenant = me.Principal?.TenantId;
    var p = await db.Providers.AsNoTracking()
        .Include(x => x.Contracts).ThenInclude(c => c.ServiceLines)
        .FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
    if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    var today = calendar.Today();   // 18.A3
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
    if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
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
    if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
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
// 19.1b — network tiers + effective-dated provider tier assignment + the service-date resolver.
app.MapNetworkTiers();

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
