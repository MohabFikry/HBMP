using System.Text.Json.Nodes;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Interop.Domain.Fhir;
using Mersal.Interop.Domain.Mapping;
using Mersal.Interop.Domain.Model;
using Mersal.Interop.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Interop.Api;

/// <summary>
/// The FHIR R4 façade endpoints under <c>/fhir/r4</c> (phase 13.1). Every interaction: (1) passes the
/// <see cref="InteropGate"/> min-necessary check; (2) reads/writes the internal model through
/// <see cref="IFhirDataSource"/> under the caller's bearer token (so the owning service enforces field-level
/// minimum-necessary + record ABAC); (3) maps to/from FHIR with the pure mappers; (4) writes a hash-chained
/// audit event via <see cref="FhirAudit"/>. Reads for derived resources are allowed; WRITES exist only for the
/// safe creates and translate to the owning service's native command.
/// </summary>
public static class FhirEndpoints
{
    public static void MapFhir(this WebApplication app)
    {
        var g = app.MapGroup("/fhir/r4");

        // Capability statement — public metadata (advertises exactly the implemented interactions + SMART scopes).
        g.MapGet("/metadata", (HttpContext http) =>
            FhirResults.Ok(FhirCapability.Statement(BaseUrl(http)))).AllowAnonymous();

        // ---- Reads + searches (all nine resources) ----
        Wire<BeneficiarySource>(g, InteropPolicies.Patient,
            (s, id, b, ct) => s.ReadPatientAsync(id, b, ct), FhirMappers.Patient, patientSearch: null,
            customSearch: async (deps, http, ct) =>
            {
                var identifier = http.Request.Query["identifier"].FirstOrDefault();
                var name = http.Request.Query["name"].FirstOrDefault();
                var list = await deps.Source.SearchPatientsAsync(identifier, name, deps.Bearer(http), ct);
                return list.Select(FhirMappers.Patient);
            });

        Wire<CoverageSource>(g, InteropPolicies.Coverage,
            (s, id, b, ct) => s.ReadCoverageAsync(id, b, ct), FhirMappers.Coverage,
            patientSearch: (s, pid, b, ct) => s.SearchCoverageAsync(pid, b, ct));

        Wire<ServiceRequestSource>(g, InteropPolicies.ServiceRequest,
            (s, id, b, ct) => s.ReadServiceRequestAsync(id, b, ct), FhirMappers.ServiceRequest,
            patientSearch: (s, pid, b, ct) => s.SearchServiceRequestsAsync(pid, b, ct));

        Wire<MedicationRequestSource>(g, InteropPolicies.MedicationRequest,
            (s, id, b, ct) => s.ReadMedicationRequestAsync(id, b, ct), FhirMappers.MedicationRequest,
            patientSearch: (s, pid, b, ct) => s.SearchMedicationRequestsAsync(pid, b, ct));

        Wire<DiagnosticReportSource>(g, InteropPolicies.DiagnosticReport,
            (s, id, b, ct) => s.ReadDiagnosticReportAsync(id, b, ct), FhirMappers.DiagnosticReport,
            patientSearch: (s, pid, b, ct) => s.SearchDiagnosticReportsAsync(pid, b, ct));

        Wire<EncounterSource>(g, InteropPolicies.Encounter,
            (s, id, b, ct) => s.ReadEncounterAsync(id, b, ct), FhirMappers.Encounter,
            patientSearch: (s, pid, b, ct) => s.SearchEncountersAsync(pid, b, ct));

        Wire<ConditionSource>(g, InteropPolicies.Condition,
            (s, id, b, ct) => s.ReadConditionAsync(id, b, ct), FhirMappers.Condition,
            patientSearch: (s, pid, b, ct) => s.SearchConditionsAsync(pid, b, ct));

        Wire<ObservationSource>(g, InteropPolicies.Observation,
            (s, id, b, ct) => s.ReadObservationAsync(id, b, ct), FhirMappers.Observation,
            patientSearch: (s, pid, b, ct) => s.SearchObservationsAsync(pid, b, ct));

        Wire<AllergyIntoleranceSource>(g, InteropPolicies.AllergyIntolerance,
            (s, id, b, ct) => s.ReadAllergyAsync(id, b, ct), FhirMappers.AllergyIntolerance,
            patientSearch: (s, pid, b, ct) => s.SearchAllergiesAsync(pid, b, ct));

        // ---- Writes (safe creates only) — translate to the owning service's native command ----
        MapCreate(g, InteropPolicies.ServiceRequest, WriteTranslators.ServiceRequest,
            (s, id, b, ct) => Box(s.ReadServiceRequestAsync(id, b, ct), FhirMappers.ServiceRequest));
        MapCreate(g, InteropPolicies.MedicationRequest, WriteTranslators.MedicationRequest,
            (s, id, b, ct) => Box(s.ReadMedicationRequestAsync(id, b, ct), FhirMappers.MedicationRequest));
        MapCreate(g, InteropPolicies.Observation, WriteTranslators.Observation,
            (s, id, b, ct) => Box(s.ReadObservationAsync(id, b, ct), FhirMappers.Observation));
        MapCreate(g, InteropPolicies.AllergyIntolerance, WriteTranslators.AllergyIntolerance,
            (s, id, b, ct) => Box(s.ReadAllergyAsync(id, b, ct), FhirMappers.AllergyIntolerance));

        // ---- Reject writes to derived/immutable resources with an OperationOutcome (not silent 404) ----
        foreach (var r in FhirCapability.Resources.Where(x => !x.CanCreate))
        {
            var name = r.Name;
            g.MapPost($"/{name}", () => FhirResults.NotSupported(
                $"{name} is derived/read-only via the façade — it cannot be created through FHIR. Use the native workflow."));
        }
    }

    // Signature aliases keep the Wire() call sites readable.
    private delegate Task<T?> ReadFn<T>(IFhirDataSource s, string id, string? bearer, CancellationToken ct);
    private delegate Task<IReadOnlyList<T>> PatientSearchFn<T>(IFhirDataSource s, string patientId, string? bearer, CancellationToken ct);
    private delegate Task<IEnumerable<JsonObject>> CustomSearchFn(FhirDeps deps, HttpContext http, CancellationToken ct);
    private delegate Task<JsonObject?> ReadbackFn(IFhirDataSource s, string id, string? bearer, CancellationToken ct);

    private static void Wire<T>(
        RouteGroupBuilder g, string resource,
        ReadFn<T> read, Func<T, JsonObject> map,
        PatientSearchFn<T>? patientSearch,
        CustomSearchFn? customSearch = null)
    {
        var readAction = InteropPolicies.ReadAction(resource);
        var sensitive = IsSensitive(resource);

        // READ  GET /fhir/r4/{Resource}/{id}
        g.MapGet($"/{resource}/{{id}}", async (string id, HttpContext http, FhirDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(readAction, $"read-{resource}", ct);
            if (denied is not null) return denied;
            var src = await read(deps.Source, id, deps.Bearer(http), ct);
            if (src is null) return FhirResults.NotFound(resource, id);
            await deps.Audit.ReadAsync(deps.Gate.Principal!, resource, id, sensitive, ct);
            return FhirResults.Ok(map(src));
        });

        // SEARCH  GET /fhir/r4/{Resource}?patient={id}  (or ?identifier/name for Patient)
        g.MapGet($"/{resource}", async (HttpContext http, FhirDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(readAction, $"search-{resource}", ct);
            if (denied is not null) return denied;

            IEnumerable<JsonObject> mapped;
            if (customSearch is not null)
            {
                mapped = await customSearch(deps, http, ct);
            }
            else
            {
                var patient = http.Request.Query["patient"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(patient))
                    return FhirResults.Outcome(StatusCodes.Status400BadRequest, "error", "required",
                        $"Search {resource} requires a 'patient' parameter.");
                var list = await patientSearch!(deps.Source, patient!, deps.Bearer(http), ct);
                mapped = list.Select(map);
            }

            var results = mapped.ToList();
            await deps.Audit.SearchAsync(deps.Gate.Principal!, resource, results.Count, sensitive, ct);
            return FhirResults.Ok(Fhir.SearchBundle(BaseUrl(http), resource, results));
        });
    }

    private static void MapCreate(
        RouteGroupBuilder g, string resource,
        Func<JsonObject?, TranslationResult> translate,
        ReadbackFn readback)
    {
        var writeAction = InteropPolicies.WriteAction(resource);

        g.MapPost($"/{resource}", async (HttpContext http, FhirDeps deps, CancellationToken ct) =>
        {
            var denied = await deps.Gate.CheckAsync(writeAction, $"create-{resource}", ct);
            if (denied is not null) return denied;

            JsonObject? body;
            try { body = await http.Request.ReadFromJsonAsync<JsonObject>(ct); }
            catch (System.Text.Json.JsonException) { return FhirResults.Outcome(StatusCodes.Status400BadRequest, "error", "structure", "Request body is not valid JSON."); }

            var translation = translate(body);
            if (!translation.Ok)
                return FhirResults.Outcome(StatusCodes.Status422UnprocessableEntity, "error", "invalid", Diagnostics(translation.Error));

            var bearer = deps.Bearer(http);
            var tenant = deps.Gate.Principal!.TenantId;

            // Idempotency: FHIR If-None-Exist (or Idempotency-Key) → a replayed create returns the prior resource,
            // never a second downstream command.
            var token = http.Request.Headers["If-None-Exist"].FirstOrDefault()
                        ?? http.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
            {
                var key = $"{resource}:{tenant}:{token}";
                var prior = await deps.Db.FhirCreates.AsNoTracking().FirstOrDefaultAsync(x => x.DedupeKey == key, ct);
                if (prior is not null)
                {
                    var priorResource = prior.CreatedResourceId is null ? null : await readback(deps.Source, prior.CreatedResourceId, bearer, ct);
                    return FhirResults.Ok(priorResource ?? Stub(resource, prior.CreatedResourceId));
                }

                var write = await deps.Source.CreateAsync(resource, translation.Command!, bearer, token, ct);
                if (!write.Ok) return MapWriteFailure(resource, write);
                deps.Db.FhirCreates.Add(new FhirCreateRecord
                {
                    DedupeKey = key, ResourceType = resource, CreatedResourceId = write.CreatedId,
                    TenantId = tenant, StatusCode = write.Status, CreatedAt = DateTimeOffset.UtcNow,
                });
                await deps.Db.SaveChangesAsync(ct);
                await deps.Audit.CreateAsync(deps.Gate.Principal!, resource, write.CreatedId, ct);
                var created = write.CreatedId is null ? null : await readback(deps.Source, write.CreatedId, bearer, ct);
                return FhirResults.Created(created ?? Stub(resource, write.CreatedId), $"{BaseUrl(http)}/{resource}/{write.CreatedId}");
            }

            // Non-idempotent create.
            var result = await deps.Source.CreateAsync(resource, translation.Command!, bearer, null, ct);
            if (!result.Ok) return MapWriteFailure(resource, result);
            await deps.Audit.CreateAsync(deps.Gate.Principal!, resource, result.CreatedId, ct);
            var resource2 = result.CreatedId is null ? null : await readback(deps.Source, result.CreatedId, bearer, ct);
            return FhirResults.Created(resource2 ?? Stub(resource, result.CreatedId), $"{BaseUrl(http)}/{resource}/{result.CreatedId}");
        });
    }

    private static async Task<JsonObject?> Box<T>(Task<T?> read, Func<T, JsonObject> map)
    {
        var src = await read;
        return src is null ? null : map(src);
    }

    private static IResult MapWriteFailure(string resource, SiblingWriteResult w) => w.Status switch
    {
        401 => FhirResults.Unauthenticated(),
        403 => FhirResults.Forbidden($"The owning service refused the {resource} create."),
        404 => FhirResults.NotFound(resource, "(subject)"),
        409 => FhirResults.Outcome(StatusCodes.Status409Conflict, "error", "conflict", $"The {resource} create conflicted downstream."),
        _ => FhirResults.Outcome(StatusCodes.Status502BadGateway, "error", "exception", $"The owning service could not create the {resource} (status {w.Status})."),
    };

    private static JsonObject Stub(string resource, string? id) => Fhir.Resource(resource, id);

    private static string Diagnostics(JsonObject? outcome) =>
        (outcome?["issue"] as JsonArray)?.FirstOrDefault()?["diagnostics"]?.GetValue<string>() ?? "Invalid resource.";

    private static bool IsSensitive(string resource) =>
        resource is InteropPolicies.Condition or InteropPolicies.DiagnosticReport or InteropPolicies.Observation
            or InteropPolicies.MedicationRequest or InteropPolicies.AllergyIntolerance;

    private static string BaseUrl(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/fhir/r4";
}
