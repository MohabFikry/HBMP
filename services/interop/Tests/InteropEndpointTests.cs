using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Mersal.Audit.Client;

namespace Mersal.Interop.Tests;

/// <summary>End-to-end façade tests through the real web host (fake data source + capturing audit). These prove
/// the 13.1 acceptance criteria: a valid mapped read with a PHI-read audit; min-necessary parity (Finance cannot
/// reach Condition); a ServiceRequest create that round-trips; derived resources reject writes. Reads/creates
/// here touch no database — the façade owns none.</summary>
public class InteropEndpointTests(InteropFactory factory) : IClassFixture<InteropFactory>
{
    private const string P = FakeFhirDataSource.PatientId;

    [Fact] // acceptance #1
    public async Task Doctor_reads_Patient_and_a_phi_read_is_audited()
    {
        var client = factory.ClientFor("doctor", "fhir:read:Patient");
        var resp = await client.GetAsync($"/fhir/r4/Patient/{P}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
        body["resourceType"]!.GetValue<string>().Should().Be("Patient");
        body["identifier"]!.AsArray().Should().NotBeEmpty();

        factory.Audit.Fhir.Should().Contain(e =>
            e.EntityType == "fhir:Patient" && e.Action == AuditAction.Read && e.EntityId == P);
    }

    [Fact] // acceptance #2
    public async Task Finance_cannot_read_Condition_via_fhir()
    {
        var client = factory.ClientFor("finance", "fhir:read:Condition");

        var search = await client.GetAsync($"/fhir/r4/Condition?patient={P}");
        search.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var oo = JsonNode.Parse(await search.Content.ReadAsStringAsync())!.AsObject();
        oo["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");

        var read = await client.GetAsync("/fhir/r4/Condition/D-1");
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Doctor_can_read_Condition_via_fhir()
    {
        var client = factory.ClientFor("doctor", "fhir:read:Condition");
        var resp = await client.GetAsync($"/fhir/r4/Condition?patient={P}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
        bundle["type"]!.GetValue<string>().Should().Be("searchset");
        bundle["entry"]!.AsArray()[0]!["resource"]!["code"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("E11.9");
    }

    [Fact] // acceptance #3
    public async Task Doctor_creates_ServiceRequest_which_round_trips()
    {
        var client = factory.ClientFor("doctor", "fhir:write:ServiceRequest");
        var payload = """
        {
          "resourceType": "ServiceRequest",
          "status": "draft",
          "subject": { "reference": "Patient/MRS-M-1" },
          "code": { "coding": [ { "system": "http://loinc.org", "code": "24323-8", "display": "Metabolic panel" } ] },
          "quantityQuantity": { "value": 1 }
        }
        """;
        var resp = await client.PostAsync("/fhir/r4/ServiceRequest",
            new StringContent(payload, Encoding.UTF8, "application/fhir+json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
        body["resourceType"]!.GetValue<string>().Should().Be("ServiceRequest");
        body["subject"]!["reference"]!.GetValue<string>().Should().Be($"Patient/{P}");

        // The façade translated to a NATIVE command (not raw FHIR) before calling the owning service.
        factory.Source.LastCreateResource.Should().Be("ServiceRequest");
        factory.Source.LastCreateCommand!["beneficiaryId"]!.GetValue<string>().Should().Be(P);
        factory.Audit.Fhir.Should().Contain(e => e.EntityType == "fhir:ServiceRequest" && e.Action == AuditAction.Create);
    }

    [Fact]
    public async Task Writes_to_derived_resources_are_rejected_with_OperationOutcome()
    {
        var client = factory.ClientFor("doctor", "fhir:write:DiagnosticReport");
        var resp = await client.PostAsync("/fhir/r4/DiagnosticReport",
            new StringContent("""{ "resourceType": "DiagnosticReport" }""", Encoding.UTF8, "application/fhir+json"));

        resp.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        var oo = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
        oo["issue"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("not-supported");
    }

    [Fact]
    public async Task Metadata_is_public_and_lists_capabilities()
    {
        var resp = await factory.CreateClient().GetAsync("/fhir/r4/metadata");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var stmt = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
        stmt["resourceType"]!.GetValue<string>().Should().Be("CapabilityStatement");
        stmt["rest"]!.AsArray()[0]!["resource"]!.AsArray().Should().HaveCount(9);
    }

    [Fact]
    public async Task Missing_scope_is_forbidden()
    {
        var client = factory.ClientFor("doctor"); // no fhir:read:Patient scope
        var resp = await client.GetAsync($"/fhir/r4/Patient/{P}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact] // idempotency ledger — needs a real DB
    public async Task Create_with_If_None_Exist_is_idempotent()
    {
        Skip.If(InteropFactory.Db is null, "test DB not configured — set INTEROP_TEST_DB to run this DB integration test.");
        var client = factory.ClientFor("doctor", "fhir:write:Observation");
        client.DefaultRequestHeaders.Add("If-None-Exist", $"obs-{Guid.NewGuid()}"); // unique per run → always a fresh first-create
        var payload = """
        {
          "resourceType": "Observation",
          "subject": { "reference": "Patient/MRS-M-1" },
          "code": { "coding": [ { "system": "http://loinc.org", "code": "8867-4" } ] },
          "valueQuantity": { "value": 72, "unit": "beats/minute" },
          "category": [ { "coding": [ { "code": "vital-signs" } ] } ]
        }
        """;

        var first = await client.PostAsync("/fhir/r4/Observation", new StringContent(payload, Encoding.UTF8, "application/fhir+json"));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var replay = await client.PostAsync("/fhir/r4/Observation", new StringContent(payload, Encoding.UTF8, "application/fhir+json"));
        replay.StatusCode.Should().Be(HttpStatusCode.OK); // replay returns the prior resource, not a new create
    }
}
