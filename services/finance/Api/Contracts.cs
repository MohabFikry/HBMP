namespace Mersal.Finance.Api;

// Request contracts for finance-service (phase 10.2). No clinical field appears anywhere.

public sealed record GenerateSettlementRequest(Guid ProviderId, DateOnly PeriodStart, DateOnly PeriodEnd);

public sealed record ExportRequest(string Report, string? Format, DateOnly From, DateOnly To,
    string? Category, Guid? ProviderId);

/// <summary>The finance projection seam — a domain event refreshes the read-model. Fields carry billing codes +
/// amounts only (any clinical key is ignored at the projection boundary).</summary>
public sealed record ProjectRequest(Guid EventId, string EventType, string TenantId,
    Dictionary<string, string> Fields, DateTimeOffset OccurredAt);
