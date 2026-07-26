using FluentAssertions;
using Mersal.Interop.Domain.Integration;

namespace Mersal.Interop.Tests;

/// <summary>Pure 13.2 tests: the DPIA gate refuses enablement without both artifacts; the referral ACL maps a
/// valid partner message to an internal event and quarantines malformed ones.</summary>
public class IntegrationDomainTests
{
    private static PartnerDescriptor Partner(DpiaStatus dpia = DpiaStatus.NotStarted, string? dsa = null) => new()
    {
        PartnerId = "digital-referral-network",
        Name = "Referral Network",
        Direction = PartnerDirection.Bidirectional,
        Transport = PartnerTransport.FhirRest,
        Dpia = dpia,
        DataSharingAgreementRef = dsa,
    };

    [Fact]
    public void DpiaGate_refuses_without_dpia_or_agreement()
    {
        DpiaGate.CanEnable(Partner()).Allowed.Should().BeFalse();
        DpiaGate.CanEnable(Partner(DpiaStatus.SignedOff)).Allowed.Should().BeFalse();      // no DSA
        DpiaGate.CanEnable(Partner(dsa: "DSA-1")).Allowed.Should().BeFalse();               // no DPIA
    }

    [Fact]
    public void DpiaGate_allows_only_with_both_artifacts()
    {
        var outcome = DpiaGate.CanEnable(Partner(DpiaStatus.SignedOff, "DSA-2026-001"));
        outcome.Allowed.Should().BeTrue();
        outcome.ReasonCode.Should().Be("ok");
    }

    [Fact]
    public void Referral_acl_maps_valid_fhir_servicerequest_to_internal_event()
    {
        var adapter = new ReferralNetworkAdapter();
        var msg = new InboundMessage("digital-referral-network", "fhir+json", """
        {
          "resourceType": "ServiceRequest",
          "intent": "order",
          "subject": { "reference": "Patient/MRS-M-9" },
          "code": { "coding": [ { "code": "394579002", "display": "Cardiology" } ] },
          "identifier": [ { "value": "EXT-REF-77" } ]
        }
        """);

        var result = adapter.Translate(msg);

        result.IsMapped.Should().BeTrue();
        result.Mapped.Should().ContainSingle();
        result.Mapped![0].Type.Should().Be("ReferralReceived");
        result.Mapped![0].PayloadJson.Should().Contain("MRS-M-9").And.Contain("394579002");
    }

    [Fact]
    public void Referral_acl_quarantines_malformed_and_incomplete_messages()
    {
        var adapter = new ReferralNetworkAdapter();
        adapter.Translate(new InboundMessage("p", "fhir+json", "not json")).IsMapped.Should().BeFalse();
        adapter.Translate(new InboundMessage("p", "fhir+json", """{ "resourceType": "Patient" }""")).IsMapped.Should().BeFalse();
        adapter.Translate(new InboundMessage("p", "fhir+json", """{ "resourceType": "ServiceRequest", "code": { "coding": [ { "code": "x" } ] } }""")).IsMapped.Should().BeFalse(); // no subject
    }

    [Fact]
    public void Referral_acl_maps_internal_ReferralCreated_outbound_and_ignores_others()
    {
        var adapter = new ReferralNetworkAdapter();
        var outbound = adapter.Map(new InternalDomainEvent("ReferralCreated",
            """{ "beneficiaryId": "MRS-M-9", "requestedSpecialtyCode": "394579002", "referralRef": "REF-2026-1" }"""));
        outbound.Should().NotBeNull();
        outbound!.Body.Should().Contain("ServiceRequest").And.Contain("Patient/MRS-M-9");

        adapter.Map(new InternalDomainEvent("SomethingElse", "{}")).Should().BeNull();
    }

    [Fact]
    public async Task Ocr_and_nlp_stubs_are_no_ops()
    {
        (await new NoOpDocumentOcrProvider().ExtractAsync(new OcrRequest([1, 2], "application/pdf", "ar"))).Extracted.Should().BeFalse();
        (await new NoOpArabicNlpExtractor().ExtractAsync("نص عربي")).Extracted.Should().BeFalse();
    }
}
