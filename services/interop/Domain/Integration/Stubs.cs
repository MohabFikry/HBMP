namespace Mersal.Interop.Domain.Integration;

/// <summary>
/// Non-functional placeholder adapters behind the interfaces (13.2). They exist so a new partner is added by
/// implementing the interface + ACL mapping ONLY — no core service changes — and so the registry can advertise
/// the roadmap partners as Disabled/DPIA-pending. Each returns "not enabled / DPIA pending" until a real ACL and
/// a DPIA + data-sharing agreement land. The <see cref="ReferralNetworkAdapter"/> shows a fully-mapped example.
/// </summary>

/// <summary>UNHCR identifier-validation (batch validation of RefugeeID/UNHCRNo — 16 External Registries).</summary>
public sealed class UnhcrIdentifierAdapter : IInboundIntegrationAdapter, IOutboundIntegrationAdapter
{
    public string PartnerId => "unhcr-identity";
    public PartnerDirection Direction => PartnerDirection.Bidirectional;
    public PartnerTransport Transport => PartnerTransport.Batch;
    public AclResult Translate(InboundMessage message) => AclResult.Quarantine("UNHCR identity adapter not enabled — DPIA pending (stub).");
    public OutboundMessage? Map(InternalDomainEvent internalEvent) => null;
}

/// <summary>Government claim/eligibility adapter (roadmap).</summary>
public sealed class GovernmentClaimAdapter : IInboundIntegrationAdapter, IOutboundIntegrationAdapter
{
    public string PartnerId => "government-claims";
    public PartnerDirection Direction => PartnerDirection.Bidirectional;
    public PartnerTransport Transport => PartnerTransport.Rest;
    public AclResult Translate(InboundMessage message) => AclResult.Quarantine("Government claims adapter not enabled — DPIA pending (stub).");
    public OutboundMessage? Map(InternalDomainEvent internalEvent) => null;
}

/// <summary>Insurer claim/eligibility adapter (roadmap).</summary>
public sealed class InsurerEligibilityAdapter : IInboundIntegrationAdapter, IOutboundIntegrationAdapter
{
    public string PartnerId => "insurer-eligibility";
    public PartnerDirection Direction => PartnerDirection.Bidirectional;
    public PartnerTransport Transport => PartnerTransport.Rest;
    public AclResult Translate(InboundMessage message) => AclResult.Quarantine("Insurer eligibility adapter not enabled — DPIA pending (stub).");
    public OutboundMessage? Map(InternalDomainEvent internalEvent) => null;
}

/// <summary>HL7 v2 referral inbound/outbound (digital referral network over v2 messaging — roadmap; the FHIR path
/// is fully mapped in <see cref="ReferralNetworkAdapter"/>).</summary>
public sealed class Hl7v2ReferralAdapter : IInboundIntegrationAdapter, IOutboundIntegrationAdapter
{
    public string PartnerId => "hl7v2-referral";
    public PartnerDirection Direction => PartnerDirection.Bidirectional;
    public PartnerTransport Transport => PartnerTransport.Hl7v2;
    public AclResult Translate(InboundMessage message) => AclResult.Quarantine("HL7 v2 referral adapter not enabled — DPIA pending (stub).");
    public OutboundMessage? Map(InternalDomainEvent internalEvent) => null;
}
