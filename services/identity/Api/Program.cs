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
builder.Services.AddAuthorization();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("identity-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "identity-service" })).AllowAnonymous();

app.MapConnect(); // 17.2 — /connect/{authorize,token,userinfo,login,logout}

// Read-only roles/scopes-as-data catalog (verification of the 17.1 seed). NOTE: the mutating admin surface
// lands in 17.4 behind admin RBAC + SoD; these reads are non-sensitive catalog metadata (no user data).
var cat = app.MapGroup("/identity");

cat.MapGet("/roles", async (IdentityStoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Roles.AsNoTracking().OrderBy(r => r.Name)
        .Select(r => new { name = r.Name, tier = r.SensitivityTier }).ToListAsync(ct)))
    .AllowAnonymous();

cat.MapGet("/scopes", async (IdentityStoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Scopes.AsNoTracking().OrderBy(s => s.Name)
        .Select(s => new { name = s.Name, domain = s.Domain, serviceOnly = s.ServiceOnly }).ToListAsync(ct)))
    .AllowAnonymous();

// The scope union a user with these roles would receive — the exact seam the 17.2 issuer uses for the
// `scope` claim. Query: /identity/effective-scopes?role=finance&role=reception
cat.MapGet("/effective-scopes", async (string[] role, RoleScopeResolver resolver, CancellationToken ct) =>
    Results.Ok((await resolver.ResolveScopesAsync(role, ct)).OrderBy(s => s, StringComparer.Ordinal)))
    .AllowAnonymous();

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

public partial class Program;
