using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Claims.Infrastructure;
using Mersal.Events;
using Mersal.Time;

namespace Mersal.Claims.Api;

/// <summary>Bundles the claims-endpoint dependencies so each handler takes one injected object.</summary>
public sealed class ClaimsDeps(
    ClaimsDbContext db, ClaimsGate gate, ClaimsQueries queries, ClaimIntakeExecutor intake,
    IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
    IBusinessCalendar calendar)
{
    public ClaimsDbContext Db { get; } = db;
    public ClaimsGate Gate { get; } = gate;
    public ClaimsQueries Queries { get; } = queries;
    public ClaimIntakeExecutor Intake { get; } = intake;
    public IAuditClient Audit { get; } = audit;
    public IOutbox Outbox { get; } = outbox;
    public IHbmpPrincipalAccessor Me { get; } = me;
    public TimeProvider Clock { get; } = clock;
    /// <summary>18.A3 — Africa/Cairo business dates; never derive a date from Clock directly.</summary>
    public IBusinessCalendar Calendar { get; } = calendar;

    public string Tenant => Me.Principal?.TenantId ?? "unknown";
    public string? Subject => Me.Principal?.Subject;
    public string? ProviderId => Me.Principal?.ProviderId;
    public string? Roles => Me.Principal is null ? null : string.Join(',', Me.Principal.Roles);

    /// <summary>Is the caller an EXTERNAL provider (design 37 §3)? Decides which projection a read serialises
    /// — the masked provider one or the staff one. Role-derived, like the gate's choice of rule, so the two
    /// cannot disagree about who a provider is: a token missing its provider id is still a provider here.</summary>
    public bool IsProviderCaller =>
        Me.Principal is not null && BranchScopeModes.ModeFor(Me.Principal) == ScopeMode.ProviderScoped;
}
