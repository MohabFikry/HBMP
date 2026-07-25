using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Events;
using Mersal.Finance.Infrastructure;

namespace Mersal.Finance.Api;

/// <summary>Bundles the finance-endpoint dependencies so each handler takes one injected object.</summary>
public sealed class FinanceDeps(
    FinanceDbContext db, FinanceGate gate, FinanceQueries queries, SettlementGenerator settlements,
    IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock)
{
    public FinanceDbContext Db { get; } = db;
    public FinanceGate Gate { get; } = gate;
    public FinanceQueries Queries { get; } = queries;
    public SettlementGenerator Settlements { get; } = settlements;
    public IAuditClient Audit { get; } = audit;
    public IOutbox Outbox { get; } = outbox;
    public IHbmpPrincipalAccessor Me { get; } = me;
    public TimeProvider Clock { get; } = clock;

    public string Tenant => Me.Principal?.TenantId ?? "unknown";
    public string? Subject => Me.Principal?.Subject;
    public string? Roles => Me.Principal is null ? null : string.Join(',', Me.Principal.Roles);
}
