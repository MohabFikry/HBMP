using Mersal.Auth;
using Mersal.Authz;
using Mersal.Data;

namespace Mersal.Admin.Api;

/// <summary>
/// The admin-access decision. Org Admin acts <c>tenant:own</c> (the resource carries the caller's tenant → TenantMatch);
/// Super Admin acts <c>global</c> — for a Super-Admin caller the gate leaves the resource tenant null so TenantMatch is
/// satisfied cross-tenant without widening Org Admin. Every admin action is flagged Sensitive in <see cref="AdminPolicies"/>,
/// so the engine audits the allow (grants, revocations, config changes, review decisions, and access-matrix reads).
/// Returns a ready 401/403 when denied, else null.
/// </summary>
public sealed class AdminGate(IHbmpPrincipalAccessor me, IAuthorizationEngine engine, RlsContext rls)
{
    public HbmpPrincipal? Principal => me.Principal;

    /// <summary>
    /// 18.B2 — resolve the target tenant for this action AND bind it to the RLS session, in that order.
    ///
    /// Binding matters now that admin-service connects as the NOBYPASSRLS <c>hbmp_app</c> role. The
    /// <c>UseHbmpRls</c> middleware binds the GUC from the principal, which is right for every ordinary
    /// caller; a Super Admin acting ON ANOTHER TENANT would then write rows that its own session cannot see
    /// and that the target tenant's policy rejects. So a legitimate cross-tenant action re-binds the GUC to
    /// the tenant it was authorised for, and an illegitimate one never reaches the database at all.
    /// </summary>
    public TenantResolution BindTenant(string? requested)
    {
        var p = me.Principal;
        if (p is null) return TenantResolution.Denied("no-tenant");
        var resolution = AdminContracts.ResolveTenantOrDeny(p, requested);
        if (resolution.IsAllowed) rls.TenantId = resolution.Tenant!;
        return resolution;
    }

    public async Task<IResult?> CheckAsync(string action, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null)
            return GateResults.Unauthenticated();

        // Super Admin operates globally → resource has no tenant (TenantMatch passes cross-tenant). Org Admin is
        // pinned to its own tenant.
        var tenantScope = p.IsInRole("super_admin") ? null : p.TenantId;
        var resource = new ResourceRef { Type = AdminPolicies.Resource, Id = null, TenantId = tenantScope };

        var decision = await engine.EvaluateAsync(new AuthzRequest(p, action, resource, "ADM"), ct);
        if (decision.IsAllowed) return null;

        return GateResults.Forbidden("urn:hbmp:admin-access-denied", detail: "You are not permitted to perform this administrative action.", reason: decision.ReasonCode);
    }
}
