namespace Mersal.Finance.Domain;

// ── The finance read-model (phase 10.2). Built from domain events (order-fulfillment, dispense, authorization) —
// NEVER by joining clinical tables. Every fact carries ONLY billing codes + quantities + amounts; there is no
// diagnosis / emr_note / lab_result / imaging_result column anywhere (finance ≠ diagnosis, 11-permission-matrix).

/// <summary>One delivered/authorized service line, valued for utilization + settlement. The beneficiary id is kept
/// for filtering/roll-up but is masked-min in every projection; the service code is a BILLING code (CPT/LOINC/ATC),
/// never a diagnosis. Populated at the subscription boundary, which projects away any clinical field a source event
/// might carry.</summary>
public sealed class UtilizationFact
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }                       // dedupe: a source event projects at most once
    public string TenantId { get; set; } = default!;
    public Guid BeneficiaryId { get; set; }                 // masked-min in projections
    public string CoverageCategory { get; set; } = "General";
    public string ServiceCode { get; set; } = default!;     // CPT / LOINC / ATC billing code — NOT a diagnosis
    public string ServiceLine { get; set; } = "General";    // Lab / Radiology / Pharmacy / …
    public Guid? ProviderId { get; set; }
    public int AuthorizedQty { get; set; }
    public int DeliveredQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineCost { get; set; }
    public DateOnly Period { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Dedupe ledger — a domain event projects at most once (idempotent read-model refresh).</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = default!;
    public DateTimeOffset ConsumedAt { get; set; }
}

/// <summary>Audited export handle (data.export, 19-audit §): masked PII, row count, filter, correlation id.</summary>
public sealed class ExportRecord
{
    public Guid ExportId { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = default!;
    public string Report { get; set; } = default!;          // utilization / settlement / summary
    public string Format { get; set; } = "csv";
    public string? Filter { get; set; }
    public int RowCount { get; set; }
    public string? RequestedBy { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
