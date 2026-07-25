namespace Mersal.Case.Domain;

/// <summary>Case + task + escalation state machines (23-state-machines). Transitions are validated in the domain so
/// an illegal move is a 409, never a silent write.</summary>
public static class CaseWorkflow
{
    private static readonly Dictionary<CaseStatus, CaseStatus[]> CaseMoves = new()
    {
        [CaseStatus.Open] = [CaseStatus.Active, CaseStatus.Closed],
        [CaseStatus.Active] = [CaseStatus.OnHold, CaseStatus.Resolved, CaseStatus.Closed],
        [CaseStatus.OnHold] = [CaseStatus.Active, CaseStatus.Closed],
        [CaseStatus.Resolved] = [CaseStatus.Active, CaseStatus.Closed],
        [CaseStatus.Closed] = [],
    };

    public static bool CanTransition(CaseStatus from, CaseStatus to) =>
        from == to || (CaseMoves.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0);

    private static readonly Dictionary<TaskState, TaskState[]> TaskMoves = new()
    {
        [TaskState.Todo] = [TaskState.InProgress, TaskState.Cancelled, TaskState.Done],
        [TaskState.InProgress] = [TaskState.Done, TaskState.Cancelled, TaskState.Todo],
        [TaskState.Done] = [],
        [TaskState.Cancelled] = [],
    };

    public static bool CanTransition(TaskState from, TaskState to) =>
        from == to || (TaskMoves.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0);

    private static readonly Dictionary<EscalationStatus, EscalationStatus[]> EscMoves = new()
    {
        [EscalationStatus.Raised] = [EscalationStatus.Acknowledged, EscalationStatus.Resolved],
        [EscalationStatus.Acknowledged] = [EscalationStatus.Resolved],
        [EscalationStatus.Resolved] = [],
    };

    public static bool CanTransition(EscalationStatus from, EscalationStatus to) =>
        from == to || (EscMoves.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0);
}
