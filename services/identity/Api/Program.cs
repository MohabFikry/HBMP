using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Events;
using Mersal.Identity.Api.Auth;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Phase 17.1 — the identity STORE; Phase 17.2 — the OpenIddict issuer on top of it.
builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.AddMersalIssuer(builder.Configuration, builder.Environment);
// 18.B3 (S3) — named policies for the admin surface + the catalog (see IdentityAdminPolicies).
builder.Services.AddIdentityAdminPolicies();
builder.Services.AddIssuerRateLimits();   // 18.B3 (S9) — per-route limits on the credential endpoints
// Phase 17.4 — audited admin actions (C3): durable outbox + hash-chained audit spine.
builder.Services.AddHbmpAuditClient("identity-service");
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<IdentityStoreDbContext>();
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("identity-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
// 18.B3 (audit R2 S7) — FIRST middleware. identity-service was the only service without it, which is the
// worst possible one to omit: it is where passwords, TOTP codes and bearer tokens are transmitted. Without
// HSTS a first visit over http:// is downgradeable, and without the redirect the token endpoint answers on
// plaintext at all. It goes before the exception handler so a failing request is still served over TLS.
app.UseHbmpTransportSecurity();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();      // 18.B3 (S9) — before auth: a rejected flood must not cost a token validation
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();      // 18.B3 (S4) — validates the token on the three rendered form POSTs
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "identity-service" })).AllowAnonymous();

app.MapConnect();  // 17.2 — /connect/{authorize,token,userinfo,login,logout}
app.MapAccount();  // 17.3 — /connect/{2fa,enroll-2fa} login UI + TOTP 2FA + recovery codes
app.MapAdmin();    // 17.4 — /identity/admin/* user+role+scope admin (bearer admin scope + MFA, audited)
app.MapSessions(); // 21.5 — /identity/me/sessions + /identity/admin/users/{id}/sessions|login-history
app.MapAccessReview(); // 21.5 — /identity/admin/access-review/{tenant} (JSON + CSV, audited as an export)

// Read-only roles/scopes-as-data catalog (verification of the 17.1 seed). The mutating admin surface is in
// 17.4 behind admin RBAC + SoD.
//
// 18.B3 (audit R2) — these three were AllowAnonymous. They hold no user data, which is why they were waved
// through, but together they are the platform's complete authorization map: every role, every scope, every
// role's sensitivity tier, and — via /effective-scopes — the exact entitlement any role combination yields.
// That is a target list. An attacker who has compromised one account learns from an unauthenticated GET which
// role to pivot to for `admin:break-glass` or `claims:export`, without touching a protected endpoint once.
// Authentication is enough here: any staff member may legitimately read the catalog, nobody else needs to.
var cat = app.MapGroup("/identity").RequireAuthorization(IdentityAdminPolicies.Authenticated);

cat.MapGet("/roles", async (IdentityStoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Roles.AsNoTracking().OrderBy(r => r.Name)
        .Select(r => new { name = r.Name, tier = r.SensitivityTier }).ToListAsync(ct)));

cat.MapGet("/scopes", async (IdentityStoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Scopes.AsNoTracking().OrderBy(s => s.Name)
        .Select(s => new { name = s.Name, domain = s.Domain, serviceOnly = s.ServiceOnly }).ToListAsync(ct)));

// The scope union a user with these roles would receive — the exact seam the 17.2 issuer uses for the
// `scope` claim. Query: /identity/effective-scopes?role=finance&role=reception[&tenant=<id>]
// 21.1b: grants are tenant-local, so the answer depends on the tenant; omitting it asks the platform default.
cat.MapGet("/effective-scopes", async (string[] role, string? tenant, RoleScopeResolver resolver, CancellationToken ct) =>
    Results.Ok((await resolver.ResolveScopesAsync(role, tenant, ct)).OrderBy(s => s, StringComparer.Ordinal)));

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
