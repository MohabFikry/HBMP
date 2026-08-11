namespace Mersal.Reporting.Domain;

// ── The reporting read-model (phase 8.2). Every fact is AGGREGATE + DE-IDENTIFIED: coded values, counts, amounts,
// timings — NO beneficiary identifiers, NO free-text clinical notes, NO row-level PHI. Financial facts carry NO
// diagnosis (finance ≠ diagnosis, 11-permission-matrix). Projected from domain events; never re-derives PHI.

public enum UtilizationDimension { Provider, Drug, Lab, Radiology }
public enum EncounterKind { Encounter, Booked, Attended, NoShow }
public enum CodeKind { Diagnosis, Medication }

/// <summary>One decided-authorization fact — powers approval-TAT + rejected-requests. Coded/timing only.</summary>
public sealed class AuthorizationFact
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = default!;
    public string AuthNo { get; set; } = default!;
    public string Priority { get; set; } = default!;
    public string Outcome { get; set; } = default!;      // Approved / PartiallyApproved / Rejected / ...
    public string? ReviewerId { get; set; }
    public string? RejectionReasonCode { get; set; }
    public long? TatSeconds { get; set; }
    public bool SlaBreached { get; set; }
    public DateOnly Period { get; set; }                 // decided date (day granularity)
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>Current-state snapshot of an in-flight authorization — powers pending-approvals (by status / priority /
/// age bucket / SLA breach). Upserted by lifecycle events; removed when terminally decided.</summary>
public sealed class PendingAuthorization
{
    public Guid AuthorizationId { get; set; }
    public string TenantId { get; set; } = default!;
    /// <summary>The business number the rest of the platform prints on the request. Null on rows projected
    /// before 0004 — the events always carried it, the table had nowhere to put it.</summary>
    public string? AuthNo { get; set; }
    /// <summary>Who holds it, once somebody does. Null while a request sits unclaimed, which is itself the
    /// answer to "why has this waited three days".</summary>
    public string? ReviewerId { get; set; }
    public string Priority { get; set; } = default!;
    public string Status { get; set; } = default!;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? SlaDueAt { get; set; }
    public bool SlaBreached { get; set; }
}

/// <summary>Clinic activity fact — powers clinic-workload + no-show. Counts per clinic/day/kind.</summary>
public sealed class EncounterFact
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = default!;
    public string ClinicId { get; set; } = default!;
    public string Kind { get; set; } = default!;         // Encounter / Booked / Attended / NoShow
    public DateOnly Period { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>Service-line utilization fact — powers utilization?dimension=provider|drug|lab|radiology.</summary>
public sealed class UtilizationFact
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Dimension { get; set; } = default!;    // Provider / Drug / Lab / Radiology
    public string Code { get; set; } = default!;         // provider id / ATC / CPT / modality code
    public DateOnly Period { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>Coded-count fact — powers top-diagnoses / top-medications. Coded counts ONLY (no patient link).</summary>
public sealed class CodeCount
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = default!;
    public string Kind { get; set; } = default!;         // Diagnosis / Medication
    public string Code { get; set; } = default!;         // ICD-10 / ATC
    public DateOnly Period { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>Financial fact — powers financial-summary. Service-code + amount ONLY; there is deliberately NO
/// diagnosis / clinical column here (finance ≠ diagnosis). A test asserts this invariant.</summary>
public sealed class FinancialFact
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = default!;
    public string ServiceLine { get; set; } = default!;
    public string ServiceCode { get; set; } = default!;
    public decimal Amount { get; set; }
    public DateOnly Period { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>Dedupe ledger — a domain event projects at most once (idempotent read-model refresh).</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = default!;
    public DateTimeOffset ConsumedAt { get; set; }
}

/// <summary>Async job handle for heavy / long-range analytics (NFR-006): operational reports run inline (p95 ≤ 3 s);
/// a long range returns a job handle the client polls for progress.</summary>
public sealed class ReportJob
{
    public Guid JobId { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = default!;
    public string Report { get; set; } = default!;
    public string Status { get; set; } = "Running";      // Running / Complete / Failed
    public int ProgressPercent { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
