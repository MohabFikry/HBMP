using Mersal.Auth;
using Mersal.Authz;
using Mersal.Case.Infrastructure;

namespace Mersal.Case.Api;

/// <summary>The case-access decision. The distinctive control here is the <b>case-assignment</b> ABAC condition
/// (10 §3.11): a Case Manager may read/act on a case — and reach that beneficiary's coordination-360 — ONLY while
/// they hold an active <c>case_assignment</c>. The gate resolves the caller's active-assignment set from the DB and
/// hands it to the engine on the <see cref="ResourceRef"/>, so the check runs at the POLICY layer (not just in the
/// controller); unassignment empties the set → immediate 403. Supervisory roles (manager / medical_director) reach
/// a case for oversight without an assignment. The engine audits every deny and every sensitive allow (360 reads,
/// assign/unassign). Returns a ready 403 when denied, else null.</summary>
public sealed class CaseGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine, AssignmentResolver assignments)
{
    public async Task<IResult?> CheckAsync(string action, Guid? caseId, string purpose, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        // Supervisory oversight: a Manager / Medical Director (who is not the assigned Case Manager) reads a case
        // via the distinct oversight action (tenant-only). The engine matches one rule per action+resource, so the
        // gate selects the variant by role — mirroring emr:read / emr:read-oversight.
        var isSupervisor = (p.IsInRole("manager") || p.IsInRole("medical_director")) && !p.IsInRole("case_manager");
        if (action == CasePolicies.Read && isSupervisor)
            action = CasePolicies.ReadOversight;

        // Resolve the caller's active assignments so the engine can evaluate case-assignment. Only meaningful for a
        // Case Manager acting on a specific case; supervisory reads carry no assignment requirement.
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        if (caseId is not null && Guid.TryParse(p.Subject, out var mgr))
            assigned = (await assignments.ActiveCaseIdsForAsync(mgr, ct)).ToHashSet(StringComparer.Ordinal);

        var resource = new ResourceRef
        {
            Type = CasePolicies.Resource,
            Id = caseId?.ToString(),
            TenantId = p.TenantId,
            AssignedCaseIds = assigned,
        };
        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource, purpose), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:case-access-denied", detail: "You are not permitted to perform this case action (no active assignment?).", reason: decision.ReasonCode);
    }

    public string? Tenant => me.Principal?.TenantId;
    public string? Subject => me.Principal?.Subject;
    public HbmpPrincipal? Principal => me.Principal;
}
