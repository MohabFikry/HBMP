using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.CallCentre.Infrastructure;
using Mersal.Events;

namespace Mersal.CallCentre.Api;

/// <summary>Bundles the call-centre endpoint dependencies so each handler takes one injected object rather than a
/// long parameter list (mirrors case's CaseDeps / approvals' DecisionDeps).</summary>
public sealed class CallDeps(
    CallCentreDbContext db, CallCentreGate gate, VerificationService verification, CallRefIssuer callRef,
    IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock)
{
    public CallCentreDbContext Db { get; } = db;
    public CallCentreGate Gate { get; } = gate;
    public VerificationService Verification { get; } = verification;
    public CallRefIssuer CallRef { get; } = callRef;
    public IAuditClient Audit { get; } = audit;
    public IOutbox Outbox { get; } = outbox;
    public IHbmpPrincipalAccessor Me { get; } = me;
    public TimeProvider Clock { get; } = clock;

    public string? Tenant => Me.Principal?.TenantId;
    public string? Subject => Me.Principal?.Subject;
    public string? Roles => Me.Principal is null ? null : string.Join(',', Me.Principal.Roles);

    /// <summary>Emit an audit event for a call-centre action, correlated by call_ref (design 19 §5). Every
    /// verification (pass AND fail), search, 360 read, and mutation calls this.</summary>
    public ValueTask AuditAsync(string entityType, string entityId, AuditAction action, string outcome,
        string? callRef, AuditSeverity severity = AuditSeverity.Info, string? before = null, string? after = null,
        IReadOnlyList<string>? fieldClasses = null, string purpose = "call-centre") =>
        Audit.EmitAsync(new AuditEventDraft
        {
            EntityType = entityType, EntityId = entityId, Action = action,
            ActorUserId = Subject, ActorRole = Roles, TenantId = Tenant,
            BeforeState = before, AfterState = after,
            DecisionOutcome = outcome, DecisionReasonCode = callRef,
            FieldClasses = fieldClasses ?? [], Purpose = purpose, Severity = severity,
        });
}
