using Mersal.Audit.Client;

namespace Mersal.Authz;

/// <summary>
/// The mandatory authorization engine for every service. Default-deny; evaluates RBAC (role+scope)
/// then ABAC (tenant, provider-ownership, treating-relationship, status) then break-glass, and audits
/// every deny (and every allow on sensitive resources / under break-glass). See phase-0 §0.4.
/// </summary>
public interface IAuthorizationEngine
{
    Task<AuthzDecision> EvaluateAsync(AuthzRequest request, CancellationToken ct = default);
}

/// <summary>
/// Native, in-process policy evaluator over a <see cref="PolicyBundle"/>. Structured so it can be
/// swapped for a Cerbos/OPA sidecar behind the same interface (ADR-0005).
/// </summary>
public sealed class DefaultAuthorizationEngine(
    PolicyBundle bundle,
    IAuditClient audit,
    IBreakGlassProvider breakGlass,
    TimeProvider clock) : IAuthorizationEngine
{
    public async Task<AuthzDecision> EvaluateAsync(AuthzRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = Evaluate(request);

        // Audit: every deny, and every allow that is sensitive or under break-glass.
        var rule = bundle.Match(request.Action, request.Resource.Type);
        if (!decision.IsAllowed)
        {
            await EmitAsync(request, decision, AuditSeverity.Warning, ct);
        }
        else if (decision.BreakGlass || (rule?.Sensitive ?? false))
        {
            await EmitAsync(request, decision,
                decision.BreakGlass ? AuditSeverity.High : AuditSeverity.Notice, ct);
        }

        return decision;
    }

    private AuthzDecision Evaluate(AuthzRequest request)
    {
        var p = request.Principal;
        var rule = bundle.Match(request.Action, request.Resource.Type);

        // Default-deny: an unmapped action/resource is denied.
        if (rule is null) return AuthzDecision.Deny("no-matching-rule");

        // RBAC: role membership + scope.
        if (rule.Roles.Count > 0 && !rule.Roles.Any(p.IsInRole))
            return AuthzDecision.Deny("role-not-permitted");
        if (rule.Scopes.Count > 0 && !rule.Scopes.Any(p.HasScope))
            return AuthzDecision.Deny("missing-scope");

        // ABAC conditions.
        var satisfied = new List<string>();
        var failed = new List<string>();
        foreach (var cond in rule.RequiredConditions)
        {
            if (Holds(cond, request)) satisfied.Add(cond);
            else failed.Add(cond);
        }

        if (failed.Count == 0)
            return AuthzDecision.Allow("rule-matched", satisfied);

        // A failing ABAC condition can be widened ONLY by an active, scoped break-glass grant.
        var grant = breakGlass.ActiveGrantFor(
            new HbmpRequestContext(p.Subject, request.Resource, clock.GetUtcNow()));
        if (grant is not null && grant.Covers(request.Resource, clock.GetUtcNow()))
        {
            satisfied.Add(AbacConditions.BreakGlass);
            return AuthzDecision.Allow("break-glass", satisfied, breakGlass: true);
        }

        return AuthzDecision.Deny($"abac-failed:{failed[0]}");
    }

    private static bool Holds(string condition, AuthzRequest r) => condition switch
    {
        AbacConditions.TenantMatch => AbacConditions.TenantMatches(r),
        AbacConditions.ProviderOwnership => AbacConditions.ProviderOwns(r),
        AbacConditions.TreatingRelationship => AbacConditions.HasTreatingRelationship(r),
        AbacConditions.CaseAssignment => AbacConditions.HasCaseAssignment(r),
        AbacConditions.BranchScope => AbacConditions.InBranchScope(r),
        AbacConditions.ResourceStatusActive => string.Equals(r.Resource.Status, "Active", StringComparison.OrdinalIgnoreCase),
        _ => false, // unknown condition → not satisfied (default-deny)
    };

    private ValueTask EmitAsync(AuthzRequest request, AuthzDecision decision, AuditSeverity severity, CancellationToken ct) =>
        audit.EmitAsync(new AuditEventDraft
        {
            EntityType = request.Resource.Type,
            EntityId = request.Resource.Id ?? "(none)",
            Action = AuditAction.Grant,
            ActorUserId = request.Principal.Subject,
            ActorRole = string.Join(',', request.Principal.Roles),
            TenantId = request.Principal.TenantId,
            ProviderId = request.Principal.ProviderId,
            SessionId = request.Principal.SessionId,
            ActorMfa = request.Principal.MfaSatisfied,
            Purpose = request.Purpose,
            BreakGlass = decision.BreakGlass,
            DecisionOutcome = decision.Effect.ToString(),
            DecisionReasonCode = decision.ReasonCode,
            Severity = severity,
        }, ct);
}
