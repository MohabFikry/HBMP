using Mersal.Case.Domain;

namespace Mersal.Case.Api;

// Request/response contracts for case-service (phase 10.1). Requests carry only what the caller supplies; responses
// are min-necessary views. The beneficiary-360 DTO itself lives in the Domain (field-scoped coordination view).

public sealed record OpenCaseRequest(
    Guid BeneficiaryId, CaseCategory Category, CasePriority? Priority, string? Summary);

public sealed record UpdateStatusRequest(CaseStatus Status);

public sealed record AssignRequest(Guid CaseManagerId);
public sealed record UnassignRequest(Guid CaseManagerId);

public sealed record EscalateRequest(string RaisedToRole, string Reason);
public sealed record EscalationUpdateRequest(EscalationStatus Status, string? ResolutionNote);

public sealed record CreateTaskRequest(string Title, string? Description, Guid? AssigneeId, DateTimeOffset? DueAt);
public sealed record UpdateTaskRequest(TaskState? Status, string? OutcomeNote, DateTimeOffset? DueAt, Guid? AssigneeId);

/// <summary>FR-ELG-007 — a Case Manager initiates a manual eligibility override with a MANDATORY reason; it is
/// audited here and delegated to eligibility-service (the source of truth). Reason blank → 422.</summary>
public sealed record EligibilityOverrideRequest(bool Eligible, string Reason, DateTimeOffset? ValidUntil);

/// <summary>A row on the My-Cases / case list (min-necessary — no clinical content).</summary>
public sealed record CaseListItem(
    Guid CaseId, string CaseNo, Guid BeneficiaryId, string Category, string Status, string Priority,
    string? Summary, DateTimeOffset OpenedAt)
{
    public static CaseListItem From(CaseFile c) =>
        new(c.CaseId, c.CaseNo, c.BeneficiaryId, c.Category.ToString(), c.Status.ToString(),
            c.Priority.ToString(), c.Summary, c.OpenedAt);
}

/// <summary>A cursor-paged case list.</summary>
public sealed record CaseListResponse(IReadOnlyList<CaseListItem> Items, string? NextCursor);

public sealed record CaseView(
    Guid CaseId, string CaseNo, Guid BeneficiaryId, string Category, string Status, string Priority,
    string? Summary, string? OpenedBy, DateTimeOffset OpenedAt,
    IReadOnlyList<AssignmentView> Assignments)
{
    public static CaseView From(CaseFile c) =>
        new(c.CaseId, c.CaseNo, c.BeneficiaryId, c.Category.ToString(), c.Status.ToString(), c.Priority.ToString(),
            c.Summary, c.OpenedBy, c.OpenedAt,
            c.Assignments.Where(a => a.Active).Select(AssignmentView.From).ToList());
}

public sealed record AssignmentView(Guid CaseManagerId, DateTimeOffset AssignedAt, bool Active)
{
    public static AssignmentView From(CaseAssignment a) => new(a.CaseManagerId, a.AssignedAt, a.Active);
}

public sealed record TaskView(
    Guid TaskId, Guid CaseId, string Title, string? Description, Guid? AssigneeId,
    DateTimeOffset? DueAt, string Status, string? OutcomeNote)
{
    public static TaskView From(CoordinationTask t) =>
        new(t.TaskId, t.CaseId, t.Title, t.Description, t.AssigneeId, t.DueAt, t.Status.ToString(), t.OutcomeNote);
}

public sealed record EscalationView(
    Guid EscalationId, Guid CaseId, string RaisedToRole, string Reason, string Status,
    DateTimeOffset RaisedAt, DateTimeOffset? ResolvedAt)
{
    public static EscalationView From(Escalation e) =>
        new(e.EscalationId, e.CaseId, e.RaisedToRole, e.Reason, e.Status.ToString(), e.RaisedAt, e.ResolvedAt);
}

/// <summary>Cross-case escalation worklist row: the escalation plus its case number and a MASKED beneficiary token
/// (min-necessary — the escalations board never shows a beneficiary name).</summary>
public sealed record EscalationListItem(
    Guid EscalationId, Guid CaseId, string CaseNo, string BeneficiaryToken, string RaisedToRole,
    string Reason, string Status, DateTimeOffset RaisedAt, DateTimeOffset? ResolvedAt)
{
    public static EscalationListItem From(Escalation e, string caseNo, Guid beneficiaryId) =>
        new(e.EscalationId, e.CaseId, caseNo, "•••" + beneficiaryId.ToString("N")[^4..], e.RaisedToRole,
            e.Reason, e.Status.ToString(), e.RaisedAt, e.ResolvedAt);
}
