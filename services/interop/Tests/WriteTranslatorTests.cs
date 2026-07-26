using System.Text.Json.Nodes;
using FluentAssertions;
using Mersal.Interop.Domain.Mapping;

namespace Mersal.Interop.Tests;

/// <summary>Inbound FHIR write → native command translation (13.1). Valid resources produce a native command;
/// malformed/derived ones produce an OperationOutcome — the façade never forwards a half-formed command.</summary>
public class WriteTranslatorTests
{
    [Fact]
    public void ServiceRequest_translates_subject_code_and_quantity()
    {
        var fhir = JsonNode.Parse("""
        {
          "resourceType": "ServiceRequest",
          "status": "draft",
          "subject": { "reference": "Patient/MRS-M-1" },
          "code": { "coding": [ { "system": "http://loinc.org", "code": "80053", "display": "Metabolic panel" } ] },
          "quantityQuantity": { "value": 2 }
        }
        """)!.AsObject();

        var r = WriteTranslators.ServiceRequest(fhir);

        r.Ok.Should().BeTrue();
        r.Command!["beneficiaryId"]!.GetValue<string>().Should().Be("MRS-M-1");
        r.Command!["code"]!.GetValue<string>().Should().Be("80053");
        r.Command!["quantity"]!.GetValue<decimal>().Should().Be(2m);
        r.Command!["intent"]!.GetValue<string>().Should().Be("order");
    }

    [Fact]
    public void ServiceRequest_without_subject_is_rejected_with_OperationOutcome()
    {
        var fhir = JsonNode.Parse("""{ "resourceType": "ServiceRequest", "code": { "coding": [ { "code": "80053" } ] } }""")!.AsObject();
        var r = WriteTranslators.ServiceRequest(fhir);
        r.Ok.Should().BeFalse();
        r.Error!["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
    }

    [Fact]
    public void Wrong_resourceType_is_rejected()
    {
        var fhir = JsonNode.Parse("""{ "resourceType": "Patient", "subject": { "reference": "Patient/1" } }""")!.AsObject();
        WriteTranslators.ServiceRequest(fhir).Ok.Should().BeFalse();
    }

    [Fact]
    public void MedicationRequest_translates_medication_and_dosage()
    {
        var fhir = JsonNode.Parse("""
        {
          "resourceType": "MedicationRequest",
          "subject": { "reference": "Patient/MRS-M-1" },
          "medicationCodeableConcept": { "coding": [ { "system": "http://www.whocc.no/atc", "code": "A10BA02", "display": "Metformin" } ] },
          "dosageInstruction": [ { "text": "500mg BID" } ],
          "dispenseRequest": { "quantity": { "value": 30 } }
        }
        """)!.AsObject();

        var r = WriteTranslators.MedicationRequest(fhir);
        r.Ok.Should().BeTrue();
        r.Command!["medicationCode"]!.GetValue<string>().Should().Be("A10BA02");
        r.Command!["dosageText"]!.GetValue<string>().Should().Be("500mg BID");
        r.Command!["quantity"]!.GetValue<decimal>().Should().Be(30m);
    }

    [Fact]
    public void AllergyIntolerance_uses_patient_reference()
    {
        var fhir = JsonNode.Parse("""
        {
          "resourceType": "AllergyIntolerance",
          "patient": { "reference": "Patient/MRS-M-1" },
          "code": { "coding": [ { "code": "227493005", "display": "Cashew nuts" } ] },
          "criticality": "high",
          "reaction": [ { "manifestation": [ { "text": "Anaphylaxis" } ] } ]
        }
        """)!.AsObject();

        var r = WriteTranslators.AllergyIntolerance(fhir);
        r.Ok.Should().BeTrue();
        r.Command!["beneficiaryId"]!.GetValue<string>().Should().Be("MRS-M-1");
        r.Command!["reaction"]!.GetValue<string>().Should().Be("Anaphylaxis");
    }
}
