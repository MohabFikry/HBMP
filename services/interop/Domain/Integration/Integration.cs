namespace Mersal.Interop.Domain.Integration;

/// <summary>Which way data flows for a partner.</summary>
public enum PartnerDirection { Inbound, Outbound, Bidirectional }

/// <summary>The wire transport a partner speaks.</summary>
public enum PartnerTransport { FhirRest, Hl7v2, Rest, Batch, File }

/// <summary>Enablement state — an adapter is <c>Disabled</c> until the DPIA gate is satisfied (13.2 guardrail).</summary>
public enum IntegrationStatus { Disabled, Enabled }

/// <summary>DPIA lifecycle for a partner integration (20-compliance §6).</summary>
public enum DpiaStatus { NotStarted, InProgress, SignedOff }

/// <summary>
/// A partner integration descriptor — pure config, no PHI. The <see cref="DpiaGate"/> reads
/// <see cref="Dpia"/> + <see cref="DataSharingAgreementRef"/> to decide whether the adapter may be enabled. The
/// core NEVER depends on a partner schema; adapters + the anti-corruption layer isolate it (16-service-architecture,
/// ADR-0016).
/// </summary>
public sealed record PartnerDescriptor
{
    public required string PartnerId { get; init; }
    public required string Name { get; init; }
    public required PartnerDirection Direction { get; init; }
    public required PartnerTransport Transport { get; init; }
    public IntegrationStatus Status { get; init; } = IntegrationStatus.Disabled;
    public DpiaStatus Dpia { get; init; } = DpiaStatus.NotStarted;
    /// <summary>Reference to the recorded data-sharing agreement (contract id / doc ref). Null ⇒ none on file.</summary>
    public string? DataSharingAgreementRef { get; init; }
    /// <summary>Whether cross-border processing applies (PDPL Law 151/2020 posture, 20 §5) — informational + audited.</summary>
    public bool CrossBorder { get; init; }
}

/// <summary>An inbound partner message BEFORE the ACL touches it — an opaque body in the partner's own format.</summary>
public sealed record InboundMessage(string PartnerId, string Format, string Body);

/// <summary>An internal domain event the ACL emits after mapping a partner message (rides the outbox).</summary>
public sealed record InternalDomainEvent(string Type, string PayloadJson);

/// <summary>The result of running a partner message through the anti-corruption layer: EITHER internal domain
/// events to emit, OR a quarantine decision (malformed/unmappable) — never a direct core-table write.</summary>
public sealed record AclResult(IReadOnlyList<InternalDomainEvent>? Mapped, string? QuarantineReason)
{
    public bool IsMapped => QuarantineReason is null && Mapped is not null;
    public static AclResult Emit(params InternalDomainEvent[] events) => new(events, null);
    public static AclResult Quarantine(string reason) => new(null, reason);
}

/// <summary>An outbound message the ACL produces from an internal event, in the partner's format.</summary>
public sealed record OutboundMessage(string PartnerId, string Format, string Body);

/// <summary>Common partner-facing identity every adapter carries.</summary>
public interface IExternalPartner
{
    string PartnerId { get; }
    PartnerDirection Direction { get; }
    PartnerTransport Transport { get; }
}

/// <summary>
/// Ingests partner data into HBMP. The adapter's ACL translates the partner model to internal domain events;
/// nothing writes core tables directly. A malformed/unmappable message is QUARANTINED, never applied.
/// </summary>
public interface IInboundIntegrationAdapter : IExternalPartner
{
    AclResult Translate(InboundMessage message);
}

/// <summary>
/// Pushes HBMP data to a partner. Subscribes to the EXISTING outbox/event stream (no new coupling in producers)
/// and maps an internal event to the partner format. Returns null when the event is not relevant to this partner.
/// </summary>
public interface IOutboundIntegrationAdapter : IExternalPartner
{
    OutboundMessage? Map(InternalDomainEvent internalEvent);
}

/// <summary>The registry describing every partner + its enablement state, and the ONLY path to enablement (which
/// runs through the <see cref="DpiaGate"/> and is audited).</summary>
public interface IExternalPartnerRegistry
{
    Task<IReadOnlyList<PartnerDescriptor>> ListAsync(CancellationToken ct = default);
    Task<PartnerDescriptor?> GetAsync(string partnerId, CancellationToken ct = default);
    Task UpsertAsync(PartnerDescriptor descriptor, CancellationToken ct = default);
    /// <summary>Attempt to enable a partner. Refused (with reason, audited by the caller) unless the DPIA gate
    /// passes: a DPIA sign-off AND a data-sharing agreement reference must both exist (13.2 guardrail).</summary>
    Task<GateOutcome> TryEnableAsync(string partnerId, CancellationToken ct = default);
    Task DisableAsync(string partnerId, CancellationToken ct = default);
}
