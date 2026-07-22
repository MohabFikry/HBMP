using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- The full HBMP substrate, wired exactly as every service will wire it (the "template"). ---
builder.Services.AddHbmpAuthentication(builder.Configuration);   // 0.2 identity + MFA
builder.Services.AddHbmpAuditClient("hello-service");            // 0.3 audit client

// 0.4 RBAC+ABAC engine — the default bundle plus this service's own "greeting" rule.
var bundle = new PolicyBundle("hello-0.5.0",
[
    .. DefaultPolicies.Bundle().Rules,
    new PolicyRule
    {
        Action = "hello:read", ResourceType = "greeting",
        Scopes = new HashSet<string> { "hello:read" },
        RequiredConditions = [AbacConditions.TenantMatch],
    },
]);
builder.Services.AddHbmpAuthorization(bundle);

builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true); // 0.5 transactional outbox

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hello-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "hello-service" }))
   .AllowAnonymous();

// The vertical slice: an authenticated + MFA'd + scope-gated request that is ABAC-authorized,
// performs ONE audited action, publishes ONE domain event via the outbox, and returns min-necessary
// data — or RFC 7807 problem+json on denial. Proves auth + authz + audit + events end to end.
app.MapGet("/api/v1/hello", async (
        IHbmpPrincipalAccessor me,
        IAuthorizationEngine authz,
        IAuditClient audit,
        IOutbox outbox,
        CancellationToken ct) =>
    {
        var principal = me.Require();

        var decision = await authz.EvaluateAsync(new AuthzRequest(
            principal, "hello:read", new ResourceRef { Type = "greeting", Id = "hello", TenantId = principal.TenantId }), ct);
        if (!decision.IsAllowed)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden", detail: decision.ReasonCode, type: "urn:hbmp:authz-denied");
        }

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "greeting", EntityId = "hello", Action = AuditAction.Read,
            ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
            ActorMfa = principal.MfaSatisfied,
        }, ct);

        await outbox.EnqueueAsync("GreetingViewed", "hello.events",
            new { subject = principal.Subject, at = DateTimeOffset.UtcNow }, ct);

        return Results.Ok(new { message = "أهلاً — hello from Mersal HBMP", subject = principal.Subject });
    })
    // hello:read is a scope-protected (MFA-required) endpoint; the greeting rule is in the authz bundle.
    .RequireAuthorization(HbmpPolicies.Scope("hello:read"));

app.Run();

// Exposed for WebApplicationFactory integration tests.
public partial class Program;
