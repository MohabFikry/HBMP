using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mersal.Interop.Domain.Model;

namespace Mersal.Interop.Infrastructure;

/// <summary>
/// Production wiring of <see cref="IFhirDataSource"/> to the native <c>/api/v1</c> endpoints of the owning
/// services, always under the caller's bearer token (defense in depth — each sibling enforces its own
/// authorization + field-level minimum-necessary). Reads are FAIL-SOFT: an unreachable or unauthorized sibling
/// degrades to null/empty, never a fabricated resource and never a throw. The mapping is deliberately tolerant of
/// missing fields so a minimal-but-valid FHIR resource is produced; exact native field wiring is verified against
/// live services in staging (the façade logic is proven in tests via a deterministic fake).
/// </summary>
public sealed class HttpFhirDataSource(IHttpClientFactory factory) : IFhirDataSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Patient (beneficiary) ----
    public async Task<BeneficiarySource?> ReadPatientAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("patient", $"/api/v1/beneficiaries/{id}", bearer, ct);
        return n is null ? null : MapBeneficiary(n);
    }

    public async Task<IReadOnlyList<BeneficiarySource>> SearchPatientsAsync(string? identifier, string? name, string? bearer, CancellationToken ct = default)
    {
        var q = identifier ?? name ?? "";
        var n = await GetAsync("eligibility", $"/api/v1/reception/search?q={Uri.EscapeDataString(q)}", bearer, ct);
        var results = n?["results"] as JsonArray ?? [];
        return results.OfType<JsonObject>().Select(MapReceptionCard).Where(b => b is not null).Cast<BeneficiarySource>().ToList();
    }

    // ---- Coverage ----
    public async Task<CoverageSource?> ReadCoverageAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("eligibility", $"/api/v1/coverage/{id}", bearer, ct);
        return n is null ? null : MapCoverage(n);
    }

    public async Task<IReadOnlyList<CoverageSource>> SearchCoverageAsync(string patientId, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("eligibility", $"/api/v1/beneficiaries/{patientId}/coverage", bearer, ct);
        return AsArray(n).Select(MapCoverage).ToList();
    }

    // ---- ServiceRequest (orders + referrals) ----
    public async Task<ServiceRequestSource?> ReadServiceRequestAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("orders", $"/api/v1/investigation-orders/{id}", bearer, ct);
        return n is null ? null : MapServiceRequest(n);
    }

    public async Task<IReadOnlyList<ServiceRequestSource>> SearchServiceRequestsAsync(string patientId, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("orders", $"/api/v1/beneficiaries/{patientId}/investigation-orders", bearer, ct);
        return AsArray(n).Select(MapServiceRequest).ToList();
    }

    // ---- MedicationRequest ----
    public async Task<MedicationRequestSource?> ReadMedicationRequestAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("pharmacy", $"/api/v1/prescriptions/{id}", bearer, ct);
        return n is null ? null : MapMedicationRequest(n);
    }

    public async Task<IReadOnlyList<MedicationRequestSource>> SearchMedicationRequestsAsync(string patientId, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("pharmacy", $"/api/v1/beneficiaries/{patientId}/prescriptions", bearer, ct);
        return AsArray(n).Select(MapMedicationRequest).ToList();
    }

    // ---- DiagnosticReport ----
    public async Task<DiagnosticReportSource?> ReadDiagnosticReportAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("orders", $"/api/v1/results/{id}", bearer, ct);
        return n is null ? null : MapDiagnosticReport(n);
    }

    public async Task<IReadOnlyList<DiagnosticReportSource>> SearchDiagnosticReportsAsync(string patientId, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("orders", $"/api/v1/beneficiaries/{patientId}/results", bearer, ct);
        return AsArray(n).Select(MapDiagnosticReport).ToList();
    }

    // ---- Encounter ----
    public async Task<EncounterSource?> ReadEncounterAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("emr", $"/api/v1/encounters/{id}", bearer, ct);
        return n is null ? null : MapEncounter(n);
    }

    public async Task<IReadOnlyList<EncounterSource>> SearchEncountersAsync(string patientId, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("emr", $"/api/v1/beneficiaries/{patientId}/encounters", bearer, ct);
        return AsArray(n).Select(MapEncounter).ToList();
    }

    // ---- Condition (diagnosis) ----
    public async Task<ConditionSource?> ReadConditionAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("emr", $"/api/v1/conditions/{id}", bearer, ct);
        return n is null ? null : MapCondition(n);
    }

    public async Task<IReadOnlyList<ConditionSource>> SearchConditionsAsync(string patientId, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("emr", $"/api/v1/beneficiaries/{patientId}/conditions", bearer, ct);
        return AsArray(n).Select(MapCondition).ToList();
    }

    // ---- Observation ----
    public async Task<ObservationSource?> ReadObservationAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("emr", $"/api/v1/observations/{id}", bearer, ct);
        return n is null ? null : MapObservation(n);
    }

    public async Task<IReadOnlyList<ObservationSource>> SearchObservationsAsync(string patientId, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("emr", $"/api/v1/beneficiaries/{patientId}/observations", bearer, ct);
        return AsArray(n).Select(MapObservation).ToList();
    }

    // ---- AllergyIntolerance ----
    public async Task<AllergyIntoleranceSource?> ReadAllergyAsync(string id, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("emr", $"/api/v1/allergies/{id}", bearer, ct);
        return n is null ? null : MapAllergy(n);
    }

    public async Task<IReadOnlyList<AllergyIntoleranceSource>> SearchAllergiesAsync(string patientId, string? bearer, CancellationToken ct = default)
    {
        var n = await GetAsync("emr", $"/api/v1/beneficiaries/{patientId}/allergies", bearer, ct);
        return AsArray(n).Select(MapAllergy).ToList();
    }

    // ---- Writes ----
    public async Task<SiblingWriteResult> CreateAsync(string resourceType, JsonObject nativeCommand, string? bearer, string? idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nativeCommand);
        var (client, path) = resourceType switch
        {
            "ServiceRequest" => ("orders", "/api/v1/investigation-orders"),
            "MedicationRequest" => ("pharmacy", "/api/v1/prescriptions"),
            "Observation" => ("emr", "/api/v1/observations"),
            "AllergyIntolerance" => ("emr", "/api/v1/allergies"),
            _ => ("", ""),
        };
        if (client.Length == 0) return new SiblingWriteResult(400, null, null);

        try
        {
            var http = factory.CreateClient(client);
            using var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(nativeCommand.ToJsonString(Json), Encoding.UTF8, "application/json"),
            };
            Authorize(req, bearer);
            if (!string.IsNullOrWhiteSpace(idempotencyKey)) req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            string? id = null;
            try { id = JsonNode.Parse(body)?["id"]?.GetValue<string>() ?? JsonNode.Parse(body)?[$"{char.ToLowerInvariant(resourceType[0])}{resourceType[1..]}Id"]?.GetValue<string>(); }
            catch (JsonException) { /* non-JSON body → no id */ }
            return new SiblingWriteResult((int)resp.StatusCode, id, body);
        }
        catch (HttpRequestException) { return new SiblingWriteResult(502, null, null); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new SiblingWriteResult(504, null, null); }
    }

    // ---- mapping helpers (tolerant of missing fields) ----
    private static BeneficiarySource MapBeneficiary(JsonObject n)
    {
        var ids = (n["identifiers"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(i => new SourceIdentifier(Str(i, "type") ?? "unknown", Str(i, "value") ?? ""))
            .Where(i => i.Value.Length > 0).ToList();
        var tel = (n["contacts"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(c => new SourceTelecom(string.Equals(Str(c, "kind"), "email", StringComparison.OrdinalIgnoreCase) ? "email" : "phone", Str(c, "value") ?? "", Str(c, "use")))
            .Where(t => t.Value.Length > 0).ToList();
        return new BeneficiarySource(
            Str(n, "beneficiaryId") ?? Str(n, "id") ?? "",
            ids,
            Str(n, "familyName") ?? Str(n, "lastName"),
            Str(n, "givenName") ?? Str(n, "firstName"),
            Date(n, "birthDate") ?? Date(n, "dateOfBirth"),
            Str(n, "gender"),
            tel,
            []);
    }

    private static BeneficiarySource? MapReceptionCard(JsonObject card)
    {
        var identity = card["identity"] as JsonObject;
        var id = identity is null ? null : Str(identity, "beneficiaryId");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var ids = new List<SourceIdentifier>();
        var memberNo = Str(identity!, "memberNo");
        if (!string.IsNullOrWhiteSpace(memberNo)) ids.Add(new SourceIdentifier("MemberNo", memberNo!));
        var display = Str(identity!, "displayName");
        return new BeneficiarySource(id!, ids, display, null, null, null, [], []);
    }

    private static CoverageSource MapCoverage(JsonObject n)
    {
        var limits = (n["remainingLimits"] as JsonArray ?? n["limits"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(l => new CoverageLimit(Str(l, "category") ?? "—", Dec(l, "annualLimit"), Dec(l, "remainingLimit") ?? Dec(l, "remaining")))
            .ToList();
        return new CoverageSource(
            Str(n, "coverageId") ?? Str(n, "id") ?? "",
            Str(n, "beneficiaryId") ?? "",
            Str(n, "status"),
            Str(n, "payorName") ?? Str(n, "sponsor") ?? "Mersal Foundation",
            Str(n, "classCategory") ?? "plan",
            Str(n, "planName") ?? Str(n, "classValue"),
            [],
            limits);
    }

    private static ServiceRequestSource MapServiceRequest(JsonObject n) => new(
        Str(n, "orderId") ?? Str(n, "id") ?? "",
        Str(n, "status") ?? "",
        Str(n, "intent") ?? "order",
        Str(n, "category"),
        Coding(n, "code"),
        Dec(n, "quantity"),
        Str(n, "quantityUnit"),
        Str(n, "beneficiaryId") ?? "",
        Str(n, "requesterId") ?? Str(n, "orderedBy"),
        Str(n, "performerId") ?? Str(n, "toProviderId"));

    private static MedicationRequestSource MapMedicationRequest(JsonObject n) => new(
        Str(n, "prescriptionId") ?? Str(n, "id") ?? "",
        Str(n, "status") ?? "",
        Coding(n, "medication") ?? Coding(n, "drug"),
        Str(n, "dosageText") ?? Str(n, "dosage"),
        Dec(n, "dispenseQuantity") ?? Dec(n, "quantity"),
        Str(n, "dispenseUnit") ?? Str(n, "unit"),
        Str(n, "beneficiaryId") ?? "",
        Str(n, "requesterId") ?? Str(n, "prescribedBy"));

    private static DiagnosticReportSource MapDiagnosticReport(JsonObject n) => new(
        Str(n, "resultId") ?? Str(n, "id") ?? "",
        Str(n, "status") ?? "",
        Coding(n, "code"),
        Str(n, "beneficiaryId") ?? "",
        Str(n, "orderId") ?? Str(n, "serviceRequestId"),
        DateTime(n, "issued") ?? DateTime(n, "completedAt"),
        Str(n, "contentType"),
        Str(n, "title"));

    private static EncounterSource MapEncounter(JsonObject n) => new(
        Str(n, "encounterId") ?? Str(n, "id") ?? "",
        Str(n, "status") ?? "",
        Str(n, "classCode") ?? Str(n, "class"),
        DateTime(n, "start") ?? DateTime(n, "scheduledStart"),
        DateTime(n, "end"),
        Str(n, "beneficiaryId") ?? "",
        Str(n, "practitionerId") ?? Str(n, "doctorId"));

    private static ConditionSource MapCondition(JsonObject n) => new(
        Str(n, "diagnosisId") ?? Str(n, "id") ?? "",
        Str(n, "clinicalStatus") ?? Str(n, "status"),
        Coding(n, "code"),
        Str(n, "beneficiaryId") ?? "",
        Str(n, "encounterId"),
        DateTime(n, "recordedDate") ?? DateTime(n, "diagnosedAt"));

    private static ObservationSource MapObservation(JsonObject n) => new(
        Str(n, "observationId") ?? Str(n, "vitalId") ?? Str(n, "id") ?? "",
        Str(n, "status") ?? "",
        Str(n, "category"),
        Coding(n, "code"),
        Dec(n, "value"),
        Str(n, "unit"),
        Str(n, "unitCode"),
        Str(n, "beneficiaryId") ?? "",
        Str(n, "encounterId"),
        DateTime(n, "effective") ?? DateTime(n, "recordedAt"));

    private static AllergyIntoleranceSource MapAllergy(JsonObject n) => new(
        Str(n, "allergyId") ?? Str(n, "id") ?? "",
        Coding(n, "code") ?? Coding(n, "allergen"),
        Str(n, "criticality") ?? Str(n, "severity"),
        Str(n, "reaction"),
        Str(n, "beneficiaryId") ?? "");

    private static CodedConcept? Coding(JsonObject n, string field)
    {
        if (n[field] is JsonObject c)
            return new CodedConcept(Str(c, "system") ?? "", Str(c, "code") ?? "", Str(c, "display"));
        var code = Str(n, $"{field}Code");
        return code is null ? null : new CodedConcept(Str(n, $"{field}System") ?? "", code, Str(n, $"{field}Display"));
    }

    private static IEnumerable<JsonObject> AsArray(JsonNode? n) =>
        n switch
        {
            JsonArray a => a.OfType<JsonObject>(),
            JsonObject o when o["items"] is JsonArray ia => ia.OfType<JsonObject>(),
            JsonObject o when o["results"] is JsonArray ra => ra.OfType<JsonObject>(),
            _ => [],
        };

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>()
        : o[key] is JsonValue nv && nv.GetValueKind() is JsonValueKind.Number ? nv.ToJsonString()
        : null;

    private static decimal? Dec(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<decimal>()
        : o[key] is JsonValue sv && sv.GetValueKind() == JsonValueKind.String && decimal.TryParse(sv.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d
        : null;

    private static DateOnly? Date(JsonObject o, string key) =>
        Str(o, key) is { } s && DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTimeOffset? DateTime(JsonObject o, string key) =>
        Str(o, key) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;

    private async Task<JsonObject?> GetAsync(string client, string path, string? bearer, CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient(client);
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            Authorize(req, bearer);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(body) as JsonObject
                   ?? (JsonNode.Parse(body) is JsonArray arr ? new JsonObject { ["items"] = arr.DeepClone() } : null);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static void Authorize(HttpRequestMessage req, string? bearer)
    {
        if (string.IsNullOrWhiteSpace(bearer)) return;
        var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
