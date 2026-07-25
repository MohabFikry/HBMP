using Mersal.Auth;
using Mersal.Authz;
using Mersal.Emr.Infrastructure;

namespace Mersal.Emr.Api;

/// <summary>Encapsulates the EMR access decision (US-030): it computes the caller's treating relationship to a
/// beneficiary (row-level, via <see cref="ITreatingRelationship"/>), hands that to the authorization engine
/// (policy-level, the treating-relationship ABAC condition), and returns a ready 403 problem+json when denied —
/// or <c>null</c> when allowed. The engine audits every deny (attempted PHI access) and every allow on these
/// sensitive rules, so callers need not audit the decision themselves.</summary>
public sealed class ClinicalGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine, ITreatingRelationship treating)
{
    public async Task<IResult?> CheckAsync(string action, string resourceType, string? resourceId, Guid beneficiaryId, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return Results.Problem(statusCode: 401, title: "unauthenticated", type: "urn:hbmp:unauthenticated");

        var treats = await treating.TreatsAsync(p.Subject, p.ProviderId, beneficiaryId, ct);
        var treatingSet = new HashSet<string>(StringComparer.Ordinal);
        if (treats) treatingSet.Add(beneficiaryId.ToString());

        // The medical-approval team reads for oversight (no treating relationship); route them to the distinct
        // oversight action. Treating clinicians (doctor/nurse) always use the treating-gated read.
        var effectiveAction = action;
        if (action == EmrPolicies.Read
            && (p.IsInRole("medical_approval") || p.IsInRole("medical_director"))
            && !(p.IsInRole("doctor") || p.IsInRole("nurse")))
            effectiveAction = EmrPolicies.ReadOversight;

        var resource = new ResourceRef
        {
            Type = resourceType, Id = resourceId, TenantId = p.TenantId,
            BeneficiaryId = beneficiaryId.ToString(), TreatingBeneficiaryIds = treatingSet,
        };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, effectiveAction, resource, "clinical-care"), ct);
        if (decision.IsAllowed) return null;

        // Denied — the engine already wrote the (attempted-PHI-access) audit event. Surface 403.
        return Results.Problem(
            statusCode: 403, title: "access-denied", type: "urn:hbmp:emr-access-denied",
            detail: "You do not have a treating relationship with this patient.",
            extensions: new Dictionary<string, object?> { ["reason"] = decision.ReasonCode });
    }
}
