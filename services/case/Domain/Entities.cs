namespace Mersal.Case.Domain;

// Case-management domain (phase 10.1; 10-role-matrix §3.11, 23-state-machines). Care/benefit coordination over an
// ASSIGNED case load. Soft-delete + history on every table; the assignment row is the ABAC access anchor.

/// <summary>Why the case exists (drives priority defaults + escalation routing).</summary>
public enum CaseCategory { Complex, Chronic, Vulnerable, Escalation }

/// <summary>Case lifecycle. Open (intake) → Active (being worked) → OnHold ↔ Active → Resolved → Closed.</summary>
public enum CaseStatus { Open, Active, OnHold, Resolved, Closed }

public enum CasePriority { Low, Normal, High, Urgent }

/// <summary>Coordination-task lifecycle (kanban): Todo → InProgress → Done, or Cancelled.</summary>
public enum TaskState { Todo, InProgress, Done, Cancelled }

/// <summary>Escalation lifecycle: Raised → Acknowledged → Resolved.</summary>
public enum EscalationStatus { Raised, Acknowledged, Resolved }

/// <summary>The case aggregate — the coordination unit for a beneficiary's benefit/care journey. Access to it (and,
/// through it, the beneficiary's coordination-360) is granted ONLY by an active <see cref="CaseAssignment"/>.</summary>
public sealed class CaseFile
{
    public Guid CaseId { get; set; }
    public string CaseNo { get; set; } = default!;                 // CASE-YYYY-XXXX
    public string TenantId { get; set; } = default!;
    public Guid BeneficiaryId { get; set; }
    public CaseCategory Category { get; set; }
    public CaseStatus Status { get; set; } = CaseStatus.Open;
    public CasePriority Priority { get; set; } = CasePriority.Normal;
    public string? Summary { get; set; }
    public string? OpenedBy { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Deleted { get; set; }                              // soft-delete (never hard-delete benefit data)
    public uint RowVersion { get; set; }                           // xmin — optimistic concurrency

    public List<CaseAssignment> Assignments { get; set; } = [];
    public List<CoordinationTask> Tasks { get; set; } = [];
    public List<Escalation> Escalations { get; set; } = [];
}

/// <summary>The ABAC anchor (10 §3.11). An ACTIVE row grants the case manager access to the case (and its
/// coordination-360); setting <see cref="UnassignedAt"/> / <see cref="Active"/>=false REVOKES it immediately. The
/// row is never deleted — the assignment history is auditable.</summary>
public sealed class CaseAssignment
{
    public Guid AssignmentId { get; set; }
    public string TenantId { get; set; } = "";                     // RLS tenant scope (ADR-0011)
    public Guid CaseId { get; set; }
    public Guid CaseManagerId { get; set; }                        // identity user id
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? UnassignedAt { get; set; }
    public bool Active { get; set; } = true;
    public string? AssignedBy { get; set; }
    public string? UnassignedBy { get; set; }
}

/// <summary>A coordination task on a case (kanban). Not clinical — a benefit/coordination to-do.</summary>
public sealed class CoordinationTask
{
    public Guid TaskId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid CaseId { get; set; }
    public string Title { get; set; } = default!;
    public string? Description { get; set; }
    public Guid? AssigneeId { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public TaskState Status { get; set; } = TaskState.Todo;
    public string? OutcomeNote { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>An escalation raised from a case to another role (Medical Approval / Director). Trackable + audited.</summary>
public sealed class Escalation
{
    public Guid EscalationId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid CaseId { get; set; }
    public string? RaisedBy { get; set; }
    public string RaisedToRole { get; set; } = default!;           // e.g. medical_approval / medical_director
    public string Reason { get; set; } = default!;
    public EscalationStatus Status { get; set; } = EscalationStatus.Raised;
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
}

/// <summary>Business-key formatter (0A §3). CASE-YYYY-NNNNNN — a 6-digit zero-padded per-year sequence, consistent
/// with the platform's other business keys (ORD-/RX-/AUTH-…). The design's CASE-YYYY-XXXX is realized this way.</summary>
public static class CaseNo
{
    public static string Format(int year, int sequence) => $"CASE-{year:D4}-{sequence:D6}";
}
