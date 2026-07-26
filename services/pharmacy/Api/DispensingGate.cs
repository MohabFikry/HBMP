using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Pharmacy.Api;

/// <summary>Dispensing-side authorization (phase 6). A pharmacist may read the dispensable-prescription queue and
/// dispense a line for their OWN dispensing pharmacy — enforced by the shared engine's provider-ownership ABAC rule
/// (<see cref="ProviderPolicies"/>/<see cref="PharmacyPolicies"/>), which audits every allow/deny. There is NO
/// treating-relationship gate here: a pharmacist does not treat the patient and a prescription is dispensable
/// network-wide. Min-necessary: this service never exposes investigation results — the pharmacy bundle grants a
/// pharmacist no orders/result actions at all. Returns a ready problem result when denied, else null.</summary>
public sealed class DispensingGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine)
{
    /// <summary>May the caller read dispensable prescriptions? (pharmacist role + scope + owns a pharmacy identity.)</summary>
    public async Task<IResult?> AuthorizeSearchAsync(CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null) return GateResults.Unauthenticated();
        if (string.IsNullOrWhiteSpace(p.ProviderId))
            return Deny("You are not associated with a dispensing pharmacy.");

        var resource = new ResourceRef { Type = "provider_queue", TenantId = p.TenantId, ProviderId = p.ProviderId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, ProviderPolicies.Actions.QueueRead, resource, "dispensing"), ct);
        return decision.IsAllowed ? null : Deny(decision.ReasonCode);
    }

    /// <summary>May the caller dispense a line? Provider-ownership (own pharmacy) — anything else → audited 403.</summary>
    public async Task<IResult?> AuthorizeDispenseAsync(CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null) return GateResults.Unauthenticated();
        if (string.IsNullOrWhiteSpace(p.ProviderId))
            return Deny("You are not associated with a dispensing pharmacy.");

        var resource = new ResourceRef { Type = "prescription_line", TenantId = p.TenantId, ProviderId = p.ProviderId };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, PharmacyPolicies.Dispense, resource, "dispensing"), ct);
        return decision.IsAllowed ? null : Deny(decision.ReasonCode);
    }

    private static IResult Deny(string reason) => GateResults.Forbidden("urn:hbmp:pharmacy-access-denied", detail: "You are not authorized to dispense this prescription.", reason: reason);
}
