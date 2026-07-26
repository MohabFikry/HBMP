using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mersal.Interop.Domain.Integration;

/// <summary>
/// A REAL anti-corruption layer example: the digital-referral-network adapter (35 §10). It speaks FHIR
/// ServiceRequest(referral) on the wire and translates BOTH directions through the ACL — the core never sees the
/// partner's schema, only internal <c>ReferralReceived</c> / <c>ReferralCreated</c> domain events. Inbound
/// messages that are malformed or missing required fields are QUARANTINED, never applied to core tables. This is
/// the template a new partner adapter follows (see README extension recipe). Still DPIA-gated: disabled until a
/// DPIA + data-sharing agreement are recorded.
/// </summary>
public sealed class ReferralNetworkAdapter : IInboundIntegrationAdapter, IOutboundIntegrationAdapter
{
    public string PartnerId => "digital-referral-network";
    public PartnerDirection Direction => PartnerDirection.Bidirectional;
    public PartnerTransport Transport => PartnerTransport.FhirRest;

    // ---- Inbound: partner FHIR ServiceRequest(referral) → internal ReferralReceived event ----
    public AclResult Translate(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        JsonObject? sr;
        try { sr = JsonNode.Parse(message.Body) as JsonObject; }
        catch (JsonException) { return AclResult.Quarantine("payload is not valid JSON"); }
        if (sr is null) return AclResult.Quarantine("payload is not a JSON object");

        if (!string.Equals(sr["resourceType"]?.GetValue<string>(), "ServiceRequest", StringComparison.Ordinal))
            return AclResult.Quarantine("expected a FHIR ServiceRequest");

        var subjectRef = sr["subject"]?["reference"]?.GetValue<string>();
        var patientId = ExtractPatientId(subjectRef);
        if (patientId is null) return AclResult.Quarantine("missing/invalid subject (Patient reference)");

        var code = (sr["code"]?["coding"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var specialtyCode = code?["code"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(specialtyCode)) return AclResult.Quarantine("missing referral code");

        var internalPayload = new JsonObject
        {
            ["beneficiaryId"] = patientId,
            ["requestedSpecialtyCode"] = specialtyCode,
            ["requestedSpecialtyDisplay"] = code?["display"]?.GetValue<string>(),
            ["source"] = PartnerId,
            ["externalReference"] = sr["identifier"]?[0]?["value"]?.GetValue<string>(),
        };
        return AclResult.Emit(new InternalDomainEvent("ReferralReceived", internalPayload.ToJsonString()));
    }

    // ---- Outbound: internal ReferralCreated event → partner FHIR ServiceRequest(referral) ----
    public OutboundMessage? Map(InternalDomainEvent internalEvent)
    {
        ArgumentNullException.ThrowIfNull(internalEvent);
        if (!string.Equals(internalEvent.Type, "ReferralCreated", StringComparison.Ordinal)) return null; // not ours

        JsonObject? p;
        try { p = JsonNode.Parse(internalEvent.PayloadJson) as JsonObject; }
        catch (JsonException) { return null; }
        if (p is null) return null;

        var sr = new JsonObject
        {
            ["resourceType"] = "ServiceRequest",
            ["status"] = "active",
            ["intent"] = "order",
            ["category"] = new JsonArray(new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["system"] = "http://snomed.info/sct", ["code"] = "306206005", ["display"] = "Referral to service" }),
            }),
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{p["beneficiaryId"]?.GetValue<string>()}" },
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["code"] = p["requestedSpecialtyCode"]?.GetValue<string>() ?? "unknown", ["display"] = p["requestedSpecialtyDisplay"]?.GetValue<string>() }),
            },
            ["identifier"] = new JsonArray(new JsonObject { ["value"] = p["referralRef"]?.GetValue<string>() }),
        };
        return new OutboundMessage(PartnerId, "fhir+json", sr.ToJsonString());
    }

    private static string? ExtractPatientId(string? reference)
    {
        const string prefix = "Patient/";
        return !string.IsNullOrWhiteSpace(reference) && reference.StartsWith(prefix, StringComparison.Ordinal)
            ? reference[prefix.Length..] : null;
    }
}
