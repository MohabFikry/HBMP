namespace Mersal.Finance.Api;

// Request contracts for finance-service (phase 10.2). No clinical field appears anywhere.

public sealed record GenerateSettlementRequest(Guid ProviderId, DateOnly PeriodStart, DateOnly PeriodEnd);

/// <param name="Report">`utilization` | `settlement` | `summary`. Selected on, not merely echoed into the
/// filename — see the handler. An unknown value is refused rather than falling back.</param>
/// <param name="Format">CSV only. Anything else is refused rather than silently substituted.</param>
/// <param name="Dimension">Which billing dimension the `summary` report groups by; ignored by the other two.
/// Defaults to `serviceline`, matching `GET /summaries`, so an export of the summary screen produces the
/// roll-up the operator was looking at rather than a different one.</param>
public sealed record ExportRequest(string Report, string? Format, DateOnly From, DateOnly To,
    string? Category, Guid? ProviderId, string? Dimension = null);

/// <summary>The finance projection seam — a domain event refreshes the read-model. Fields carry billing codes +
/// amounts only (any clinical key is ignored at the projection boundary).</summary>
public sealed record ProjectRequest(Guid EventId, string EventType, string TenantId,
    Dictionary<string, string> Fields, DateTimeOffset OccurredAt);
