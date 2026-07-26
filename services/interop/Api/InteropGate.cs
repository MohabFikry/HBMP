using Mersal.Auth;
using Mersal.Authz;
using Mersal.Interop.Domain.Fhir;

namespace Mersal.Interop.Api;

/// <summary>
/// The FHIR-façade access decision (phase 13.1). Each FHIR interaction (resource × verb) is a distinct action;
/// the coarse role+scope+tenant check runs at the POLICY layer via the engine (so every deny is audited), and
/// the min-necessary role set per resource is the boundary — see <see cref="InteropPolicies"/>. Field- and
/// record-level ABAC is enforced by the OWNING service when the façade reads/writes under the caller's bearer
/// token. A denial is returned as a FHIR <c>OperationOutcome</c> (403), never a bare problem+json — FHIR clients
/// expect the FHIR error envelope.
/// </summary>
public sealed class InteropGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    public HbmpPrincipal? Principal => me.Principal;

    /// <summary>Authorize a FHIR interaction. Returns null when allowed, else a ready 401/403 OperationOutcome.</summary>
    public async Task<IResult?> CheckAsync(string action, string purpose, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return FhirResults.Unauthenticated();

        var resource = new ResourceRef { Type = InteropPolicies.Resource, TenantId = p.TenantId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource, purpose), ct);
        if (decision.IsAllowed) return null;

        return FhirResults.Forbidden(
            $"You are not permitted to perform this FHIR interaction ({action}).", decision.ReasonCode);
    }
}

/// <summary>FHIR-shaped IResult helpers (OperationOutcome envelopes with the right status codes + content type).</summary>
public static class FhirResults
{
    public const string ContentType = "application/fhir+json";

    public static IResult Ok(System.Text.Json.Nodes.JsonObject resource) =>
        Results.Content(resource.ToJsonString(), ContentType, statusCode: StatusCodes.Status200OK);

    public static IResult Created(System.Text.Json.Nodes.JsonObject resource, string location) =>
        Results.Content(resource.ToJsonString(), ContentType, statusCode: StatusCodes.Status201Created);

    public static IResult Outcome(int status, string severity, string code, string diagnostics) =>
        Results.Content(Fhir.OperationOutcome(severity, code, diagnostics).ToJsonString(), ContentType, statusCode: status);

    public static IResult Unauthenticated() =>
        Outcome(StatusCodes.Status401Unauthorized, "error", "login", "Authentication is required.");

    public static IResult Forbidden(string diagnostics, string? reason = null) =>
        Outcome(StatusCodes.Status403Forbidden, "error", "forbidden",
            reason is null ? diagnostics : $"{diagnostics} [{reason}]");

    public static IResult NotFound(string resourceType, string id) =>
        Outcome(StatusCodes.Status404NotFound, "error", "not-found", $"{resourceType}/{id} was not found or is not visible to you.");

    public static IResult NotSupported(string diagnostics) =>
        Outcome(StatusCodes.Status405MethodNotAllowed, "error", "not-supported", diagnostics);
}
