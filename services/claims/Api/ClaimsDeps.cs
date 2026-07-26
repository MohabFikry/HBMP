using Mersal.Audit.Client;
using Mersal.Auth;
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
}
