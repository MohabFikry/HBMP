using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Inventory.Api;

/// <summary>
/// 25.6 (design 42 §5/§6) — the branch-reach check for clinic stock.
///
/// The practitioner write surface is now reachable by TWO kinds of caller, and they are not owed the same
/// thing:
///
///   • <c>branch:inventory:read</c> / <c>branch:inventory:write</c> — a branch coordinator or clinics manager.
///     Both are sized to a clinic on purpose, so both must be ENFORCED to a clinic: a coordinator at Maadi
///     issuing stock at Dokki is a 403 and an audit event, not a silent success.
///
/// There is NO network-wide escape hatch here, unlike practitioner administration. Nobody administers the
/// network's stock — stock belongs to a clinic, and every caller of this service reaches exactly the clinics
/// they run.
///
/// Widening the scope group without this check would have been strictly worse than leaving the endpoints on
/// <c>provider:write</c>: it would hand every coordinator the whole network's roster while looking, in the
/// route table, like a carefully sized permission.
///
/// The branch rule itself is NOT restated here. It delegates to <see cref="AbacConditions.InBranchScope"/>
/// so that the coordinator's "equals my active branch" and the manager's "in my permitted set" stay in one
/// place — the same reason design 42 §7 rule 5 insists availability is computed once.
/// </summary>
public sealed class BranchReachGuard(IHbmpPrincipalAccessor me, BranchScopeState branch, IAuditClient audit)
{
    /// <summary>The RFC 7807 type for a refusal. Distinct from a missing scope: the caller holds the right
    /// authority and pointed it at a clinic they do not run.</summary>
    public const string ProblemType = "urn:hbmp:branch-not-in-reach";

    /// <summary>Always false for inventory: there is no network-wide stock authority. Kept as a named member
    /// rather than inlined so the ABSENCE is legible — a reader looking for the escape hatch finds the reason
    /// there isn't one, instead of concluding it was forgotten.</summary>
    public bool IsNetworkWide => false;

    /// <summary>The reach mode this caller's branch predicate takes (single active branch vs the whole
    /// permitted set). Derived from the principal exactly as every other branch site derives it.</summary>
    public ScopeMode Mode => me.Principal is null ? ScopeMode.MemberScoped : BranchScopeModes.ModeFor(me.Principal);

    /// <summary>
    /// Refuse unless the caller may act on <paramref name="branchId"/>. Returns null when allowed, or a 403
    /// problem result when not — and audits the refusal at High severity, because an attempt to administer
    /// another clinic's roster is evidence whether it was a bug or a probe (doc 40 §0 A2: nothing
    /// security-relevant is silent).
    /// </summary>
    public async Task<IResult?> RefuseUnlessInReachAsync(
        Guid branchId, string entityType, string entityId, CancellationToken ct = default)
    {
        if (CanReach(branchId)) return null;
        return await DenyAsync(entityType, entityId, "branch-not-in-reach",
            "You may administer practitioners only at the branches you run.", ct);
    }

    /// <summary>
    /// The predicate, with no side effects. Pulled out so the "serves a reachable branch" check below can ask
    /// about several branches without auditing a denial for each one it tries — a refusal is ONE event about
    /// ONE decision, and emitting three would make a single 403 look like three probes in the audit trail.
    /// </summary>
    public bool CanReach(Guid branchId)
    {
        if (IsNetworkWide) return true;

        var p = me.Principal;
        if (p is null) return false;

        return AbacConditions.InBranchScope(new AuthzRequest(p, "administer", new ResourceRef
        {
            Type = "branch",
            Id = branchId.ToString(),
            TenantId = p.TenantId,
            BranchId = branchId,
            PermittedBranchIds = branch.Context.PermittedBranchIds,
            ActiveBranchId = branch.Context.ActiveBranchId,
            BranchReach = Mode,
        }));
    }

    private async Task<IResult> DenyAsync(
        string entityType, string entityId, string reason, string detail, CancellationToken ct)
    {
        var p = me.Principal;
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = entityType, EntityId = entityId, Action = AuditAction.Grant,
            ActorUserId = p?.Subject, TenantId = p?.TenantId, ActorMfa = p?.MfaSatisfied ?? false,
            DecisionOutcome = "BranchReachDenied", DecisionReasonCode = reason,
            Severity = AuditSeverity.High,
        }, ct);

        return Results.Problem(statusCode: 403, title: "branch-not-in-reach", type: ProblemType, detail: detail);
    }

    /// <summary>The branches this caller may read, for list endpoints. See <see cref="ReadableBranches"/>.</summary>
    /// <summary>The branches this caller may read, for list endpoints. Null is impossible here — inventory has
    /// no unrestricted caller — so a null return would be a bug, and the sentinel keeps it fail-closed.</summary>
    public IReadOnlySet<Guid> ReadableBranches(Guid? requestedFilter = null) =>
        BranchQueryScope.PermittedFor(Mode, branch.Context, requestedFilter)
        ?? new HashSet<Guid> { RowScope.NoBranchSentinel };
}
