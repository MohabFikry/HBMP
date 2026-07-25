using System.Text.Json;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Audit.Client;

namespace Mersal.Admin.Api;

/// <summary>
/// Session / device policy configuration and staged ABAC policy proposals (phase 8b.1). Session + device policies
/// are effective-dated (a change appends a new row, never rewrites history); a policy proposal only STAGES a diff —
/// it never hot-patches live ABAC (deployment goes through the audited CI path). Every change is audited.
/// </summary>
public sealed class PolicyConfigService(AdminDbContext db, IAuditClient audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SessionPolicy> SetSessionPolicyAsync(ActorContext actor, string tenant, SensitivityTier tier,
        int tokenTtl, int idleTimeout, int absoluteCap, int maxConcurrent, bool stepUp, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var policy = new SessionPolicy
        {
            PolicyId = Guid.NewGuid(), TenantId = tenant, RoleTier = tier,
            AccessTokenTtlSeconds = tokenTtl, IdleTimeoutSeconds = idleTimeout, AbsoluteCapSeconds = absoluteCap,
            MaxConcurrentSessions = maxConcurrent, StepUpRequired = stepUp,
            EffectiveFrom = now, UpdatedBy = actor.UserId, UpdatedAt = now,
        };
        db.SessionPolicies.Add(policy);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "session_policy", EntityId = policy.PolicyId.ToString(), Action = AuditAction.Update,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            AfterState = JsonSerializer.Serialize(new { tier = tier.ToString(), tokenTtl, idleTimeout, absoluteCap, maxConcurrent, stepUp }, Json),
            Purpose = "session-policy", Severity = AuditSeverity.Notice,
        }, ct);
        return policy;
    }

    public async Task<DevicePolicy> SetDevicePolicyAsync(ActorContext actor, string tenant, string role,
        bool requireManagedDevice, IReadOnlyList<string> ipCidrs, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var policy = new DevicePolicy
        {
            PolicyId = Guid.NewGuid(), TenantId = tenant, Role = role.Trim().ToLowerInvariant(),
            RequireManagedDevice = requireManagedDevice, IpAllowListJson = JsonSerializer.Serialize(ipCidrs, Json),
            EffectiveFrom = now, UpdatedBy = actor.UserId, UpdatedAt = now,
        };
        db.DevicePolicies.Add(policy);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "device_policy", EntityId = policy.PolicyId.ToString(), Action = AuditAction.Update,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = tenant, ActorMfa = actor.Mfa,
            AfterState = JsonSerializer.Serialize(new { role = policy.Role, requireManagedDevice, ipCidrs }, Json),
            Purpose = "device-policy", Severity = AuditSeverity.Notice,
        }, ct);
        return policy;
    }

    /// <summary>Stage a policy-bundle change as a diff — PROPOSED only, never deployed here (Security + DPO review
    /// via CI). Audited.</summary>
    public async Task<PolicyProposal> ProposeAsync(ActorContext actor, string baseVersion, string proposedVersion,
        string diffJson, string rationale, CancellationToken ct = default)
    {
        var proposal = new PolicyProposal
        {
            ProposalId = Guid.NewGuid(), BaseVersion = baseVersion, ProposedVersion = proposedVersion,
            DiffJson = diffJson, Rationale = rationale, Status = ProposalStatus.Proposed,
            ProposedBy = actor.UserId, ProposedAt = clock.GetUtcNow(),
        };
        db.PolicyProposals.Add(proposal);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "policy_proposal", EntityId = proposal.ProposalId.ToString(), Action = AuditAction.Create,
            ActorUserId = actor.UserId, ActorRole = actor.Role, TenantId = actor.TenantId, ActorMfa = actor.Mfa,
            AfterState = JsonSerializer.Serialize(new { baseVersion, proposedVersion, rationale }, Json),
            Purpose = "policy-proposal", Severity = AuditSeverity.Notice,
        }, ct);
        return proposal;
    }
}
