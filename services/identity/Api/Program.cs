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

// The SPA is served from another origin (:5173) and reaches the issuer directly, because the token's `iss`
// has to be the issuer as the BROWSER sees it (apps/web/src/config.ts). Two legs of the auth-code+PKCE flow
// are therefore cross-origin XHR — the discovery document and the /connect/token code exchange — and both
// were being blocked, so login failed before a password was ever typed. /connect/authorize is a top-level
// navigation and never needed CORS, which is why the flow looked half-alive.
//
// Origins are derived from the SAME config as the registered redirect URIs rather than listed separately:
// an origin allowed here but not registered there (or vice versa) is exactly the drift that produces a
// login which fails at a different step depending on which of the two is wrong.
const string SpaCorsPolicy = "hbmp-spa";
var spaOrigins = new[]
    {
        builder.Configuration["Issuer:WebRedirectUri"] ?? "http://localhost:5173/",
        builder.Configuration["Issuer:WebPostLogoutUri"] ?? "http://localhost:5173/",
    }
    .Select(uri => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        ? parsed.GetLeftPart(UriPartial.Authority)
        : null)
    .Where(origin => !string.IsNullOrEmpty(origin))
    .Distinct()
    .ToArray()!;

builder.Services.AddCors(options => options.AddPolicy(SpaCorsPolicy, policy => policy
    .WithOrigins(spaOrigins!)
    .WithMethods("GET", "POST", "OPTIONS")
    // No credentials: the SPA carries the token in the Authorization header, not cookies — matching the
    // gateway's own CORS stance in infra/compose/config/kong.yml.
    .WithHeaders("Authorization", "Content-Type")));

var app = builder.Build();
// 18.B3 (audit R2 S7) — FIRST middleware. identity-service was the only service without it, which is the
// worst possible one to omit: it is where passwords, TOTP codes and bearer tokens are transmitted. Without
// HSTS a first visit over http:// is downgradeable, and without the redirect the token endpoint answers on
// plaintext at all. It goes before the exception handler so a failing request is still served over TLS.
app.UseHbmpTransportSecurity();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Before the rate limiter and auth: a preflight carries no Authorization header and must not be counted,
// throttled or challenged. A 429 or 401 without CORS headers on the preflight reaches the page as an opaque
// network error, which is indistinguishable from the server being down.
app.UseCors(SpaCorsPolicy);
app.UseRateLimiter();      // 18.B3 (S9) — before auth: a rejected flood must not cost a token validation
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();      // 18.B3 (S4) — validates the token on the three rendered form POSTs
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "identity-service" })).AllowAnonymous();

app.MapConnect();  // 17.2 — /connect/{authorize,token,userinfo,login,logout}
app.MapAccount();  // 17.3 — /connect/{2fa,enroll-2fa} login UI + TOTP 2FA + recovery codes
app.MapAdmin();    // 17.4 — /identity/admin/* user+role+scope admin (bearer admin scope + MFA, audited)

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
// `scope` claim. Query: /identity/effective-scopes?role=finance&role=reception
cat.MapGet("/effective-scopes", async (string[] role, RoleScopeResolver resolver, CancellationToken ct) =>
    Results.Ok((await resolver.ResolveScopesAsync(role, ct)).OrderBy(s => s, StringComparer.Ordinal)));

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
