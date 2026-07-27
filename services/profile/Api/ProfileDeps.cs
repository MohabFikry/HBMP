using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Profile.Domain;
using Mersal.Profile.Infrastructure;

namespace Mersal.Profile.Api;

/// <summary>Bundles the profile endpoint dependencies so each handler takes one injected object rather than a
/// long parameter list (mirrors callcentre's CallDeps / case's CaseDeps).</summary>
public sealed class ProfileDeps(
    ProfileComposer composer,
    IProfileFactResolver facts,
    ICallVerificationGate verification,
    IAuthorizationEngine engine,
    IAuditClient audit,
    IHbmpPrincipalAccessor me,
    IHttpContextAccessor http)
{
    public ProfileComposer Composer { get; } = composer;
    public IProfileFactResolver Facts { get; } = facts;
    public ICallVerificationGate Verification { get; } = verification;
    public IAuthorizationEngine Engine { get; } = engine;
    public IAuditClient Audit { get; } = audit;
    public IHbmpPrincipalAccessor Me { get; } = me;

    public HbmpPrincipal? Principal => Me.Principal;
    public string? Tenant => Me.Principal?.TenantId;
    public string? Subject => Me.Principal?.Subject;
    public string? Roles => Me.Principal is null ? null : string.Join(',', Me.Principal.Roles);

    /// <summary>The caller's OWN credentials, lifted off the incoming request. This is the only token that ever
    /// leaves this service (design 39 §7.2).</summary>
    public CallerCredentials Caller()
    {
        var request = http.HttpContext?.Request;
        return new CallerCredentials(
            request?.Headers.Authorization.ToString() ?? string.Empty,
            request?.Headers["X-Active-Branch"].ToString(),
            request?.Headers["X-Correlation-Id"].ToString());
    }

    /// <summary>Coarse RBAC before anything else. Section shaping is the second, independent layer.</summary>
    public async Task<IResult?> AuthorizeAsync(string action, Guid beneficiaryId, string purpose, CancellationToken ct)
    {
        var p = Principal;
        if (p is null) return GateResults.Unauthenticated();

        var decision = await Engine.EvaluateAsync(new AuthzRequest(p, action, new ResourceRef
        {
            Type = action == ProfilePolicies.Photo ? ProfilePolicies.PhotoResource : ProfilePolicies.Resource,
            Id = beneficiaryId.ToString(),
            TenantId = p.TenantId,
            BeneficiaryId = beneficiaryId.ToString(),
        }, purpose), ct);

        return decision.IsAllowed ? null : GateResults.Forbidden("urn:hbmp:profile-denied",
            detail: "You are not permitted to open this patient profile.", reason: decision.ReasonCode);
    }
}
