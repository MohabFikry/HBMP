using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Case.Infrastructure;
using Mersal.Events;

namespace Mersal.Case.Api;

/// <summary>Bundles the case-endpoint dependencies so each handler takes one injected object rather than a long
/// parameter list (mirrors approvals' DecisionDeps).</summary>
public sealed class CaseDeps(
    CaseDbContext db, CaseGate gate, AssignmentResolver assignments, CaseNoIssuer caseNo,
    IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock)
{
    public CaseDbContext Db { get; } = db;
    public CaseGate Gate { get; } = gate;
    public AssignmentResolver Assignments { get; } = assignments;
    public CaseNoIssuer CaseNo { get; } = caseNo;
    public IAuditClient Audit { get; } = audit;
    public IOutbox Outbox { get; } = outbox;
    public IHbmpPrincipalAccessor Me { get; } = me;
    public TimeProvider Clock { get; } = clock;

    public string? Tenant => Me.Principal?.TenantId;
    public string? Subject => Me.Principal?.Subject;
    public string? Roles => Me.Principal is null ? null : string.Join(',', Me.Principal.Roles);
}
