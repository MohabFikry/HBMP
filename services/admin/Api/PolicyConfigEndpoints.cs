using Mersal.Auth.Authorization;
using Mersal.Authz;

namespace Mersal.Admin.Api;

/// <summary>Session / device policy + policy-proposal endpoints (phase 8b.1). Session + device policies are
/// effective-dated; a policy proposal only stages a diff (never hot-patches live ABAC).</summary>
public static class PolicyConfigEndpoints
{
    public static void MapPolicyConfig(this WebApplication app)
    {
        // 18.B3 (audit R2 S3) — the framework gate. Until now these groups carried NO .RequireAuthorization,
        // so an UNAUTHENTICATED request reached the handler and was rejected only by AdminGate's in-handler
        // check. That worked, but it made the whole surface depend on every handler remembering to call the
        // gate first, and it never enforced MFA at the pipeline. Group scope = admin:read (authn + admin-ness +
        // MFA); mutations add admin:write on top; AdminGate stays as layer two for the per-action rule + audit.
        var g = app.MapGroup("/api/v1/admin").WithTags("admin-policy-config").RequireAuthorization(HbmpPolicies.Scope("admin:read"));
        var w = g.MapGroup("").RequireAuthorization(HbmpPolicies.Scope("admin:write"));

        w.MapPut("/session-policy", async (SessionPolicyRequest req, AdminGate gate, PolicyConfigService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Configure, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;

            var policy = await svc.SetSessionPolicyAsync(AdminContracts.Actor(p), tenant, req.Tier,
                req.AccessTokenTtlSeconds, req.IdleTimeoutSeconds, req.AbsoluteCapSeconds,
                req.MaxConcurrentSessions, req.StepUpRequired, ct);
            return Results.Ok(new { policy.PolicyId, tier = policy.RoleTier.ToString(), policy.EffectiveFrom });
        });

        w.MapPut("/device-policy", async (DevicePolicyRequest req, AdminGate gate, PolicyConfigService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.Configure, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;

            var policy = await svc.SetDevicePolicyAsync(AdminContracts.Actor(p), tenant, req.Role,
                req.RequireManagedDevice, req.IpAllowList, ct);
            return Results.Ok(new { policy.PolicyId, policy.Role, policy.EffectiveFrom });
        });

        // Stage a policy-bundle change — Super Admin only; proposes/diffs, never deploys.
        w.MapPost("/policy-proposals", async (PolicyProposalRequest req, AdminGate gate, PolicyConfigService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.ProposePolicy, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;

            var proposal = await svc.ProposeAsync(AdminContracts.Actor(p), req.BaseVersion, req.ProposedVersion,
                req.DiffJson, req.Rationale, ct);
            return Results.Created($"/api/v1/admin/policy-proposals/{proposal.ProposalId}",
                new { proposal.ProposalId, proposal.ProposedVersion, status = proposal.Status.ToString() });
        });
    }
}
