namespace Mersal.Audit.Client;

/// <summary>
/// The canonical action taxonomy for audit events (19-audit-strategy.md §11 event catalog,
/// 22-data-dictionary.md §10.4). Every mutation/decision/consume/dispense/export/PHI-read maps here.
/// </summary>
public enum AuditAction
{
    Create,
    Update,
    SoftDelete,
    StateChange,
    Consume,
    Dispense,
    Decision,
    Read,
    Login,
    Grant,
    Export,
}

/// <summary>Audit severity — break-glass and integrity events are high/critical.</summary>
public enum AuditSeverity
{
    Info,
    Notice,
    Warning,
    High,
    Critical,
}
