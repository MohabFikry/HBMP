namespace Mersal.Emr.Domain;

/// <summary>Reception walk-in queue lifecycle (23 §6: CheckedIn → InConsultation → Completed; removed on
/// cancel/no-show). Distinct from the phase-2.3 clinician <see cref="QueueEntry"/> worklist — this queue is
/// per clinic/doctor and drives reception call-next.</summary>
public enum QueueTicketState { Waiting, InConsultation, Done, Removed }

/// <summary>A queue ticket for a checked-in appointment or a walk-in. Carries only minimum-necessary display
/// identity (<see cref="MemberNo"/>/<see cref="DisplayName"/>) captured at check-in — NEVER clinical/EMR
/// data. Scoped by (location, provider, optional doctor).</summary>
public sealed class QueueTicket
{
    public Guid QueueId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011)
    public Guid AppointmentId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? BranchId { get; set; }   // phase 14 — Mersal branch (NULL = external provider location)
    public Guid? DoctorId { get; set; }
    public string? MemberNo { get; set; }
    public string? DisplayName { get; set; }
    public AppointmentType AppointmentType { get; set; }
    public int Priority { get; set; }
    public QueueTicketState State { get; set; } = QueueTicketState.Waiting;
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? CalledAt { get; set; }
}

/// <summary>Queue ordering + transition rules. Order is priority-desc then arrival (FIFO within a priority).</summary>
public static class QueueRules
{
    /// <summary>Higher priority first, then earliest arrival. Deterministic tiebreak on the ticket id.</summary>
    public static IEnumerable<QueueTicket> Ordered(IEnumerable<QueueTicket> waiting)
        => waiting.Where(t => t.State == QueueTicketState.Waiting)
                  .OrderByDescending(t => t.Priority).ThenBy(t => t.EnqueuedAt).ThenBy(t => t.QueueId);

    public static bool CanCall(QueueTicketState s) => s == QueueTicketState.Waiting;
    public static bool CanRequeue(QueueTicketState s) => s == QueueTicketState.InConsultation;
    /// <summary>A ticket may be removed while it is still active (waiting or in consultation).</summary>
    public static bool CanRemove(QueueTicketState s) => s is QueueTicketState.Waiting or QueueTicketState.InConsultation;
}
