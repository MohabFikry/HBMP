using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Provider.Api;

/// <summary>
/// 25.2 (design 42 §2) — the branch-reach check for practitioner administration.
///
/// The practitioner write surface is now reachable by TWO kinds of caller, and they are not owed the same
/// thing:
///
///   • <c>provider:write</c> — the Network Team / Org Admin. Network-wide by definition; the branch dimension
///     does not narrow them, and it never did.
///   • <c>branch:practitioner:write</c> — a branch coordinator or clinics manager. Sized to a clinic on
///     purpose, so it must be ENFORCED to a clinic. A coordinator at Maadi assigning a practitioner to Dokki
///     is a 403 and an audit event, not a silent success.
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

    /// <summary>True when the caller's authority is network-wide, so branch reach does not apply. Checked as
    /// a SCOPE, not a role: the role list is the thing that drifts.</summary>
    public bool IsNetworkWide => me.Principal?.HasScope("provider:write") ?? false;

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

    /// <summary>
    /// Refuse unless the caller reaches at least one branch this practitioner ACTIVELY serves.
    ///
    /// This is the rule for edits that do not name a branch — a licence, a specialty, a status change. They
    /// still belong to a clinic, because the practitioner does: a coordinator maintains the licence of a
    /// doctor who works at their clinic, and has no business editing one who does not.
    ///
    /// An unassigned practitioner (no active assignment anywhere) is reachable only by a network-wide caller.
    /// That is deliberate and is the tail of D3: a coordinator may CREATE a practitioner, and the very next
    /// thing they must do is assign them to their own branch. Until they do, the row is nobody's, and
    /// "nobody's" must not mean "everybody's".
    /// </summary>
    public async Task<IResult?> RefuseUnlessServesAReachableBranchAsync(
        Guid practitionerId, IReadOnlyCollection<Guid> activeBranchIds, CancellationToken ct = default)
    {
        if (IsNetworkWide) return null;
        if (activeBranchIds.Any(CanReach)) return null;

        return await DenyAsync("practitioner", practitionerId.ToString(), "practitioner-not-at-my-branch",
            activeBranchIds.Count == 0
                ? "This practitioner is not assigned to any branch yet. Assign them to your clinic first."
                : "This practitioner does not work at a branch you run.", ct);
    }
}
