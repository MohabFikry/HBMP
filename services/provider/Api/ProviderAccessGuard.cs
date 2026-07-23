using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Provider.Api;

/// <summary>ABAC layer of provider isolation (2b.3, layer 3 of THE INVARIANT). Evaluates the reusable
/// provider-ownership policy through the platform authorization engine — so every cross-provider read is
/// denied AND audited (the engine audits denials + sensitive allows). This sits above RLS (layer 4): a
/// bug here still cannot leak because the datastore denies independently.</summary>
public sealed class ProviderAccessGuard(IAuthorizationEngine engine)
{
    /// <summary>The provider-scoped roles — a token in any of these MUST carry a provider_id claim.</summary>
    private static readonly string[] ProviderScopedRoles = ["provider_admin", "lab_tech", "imaging_tech", "pharmacist"];

    public static bool IsProviderScoped(HbmpPrincipal p) => ProviderScopedRoles.Any(p.IsInRole);

    /// <summary>A provider-scoped token with no provider_id claim is rejected outright (layer 1).</summary>
    public static bool TokenMissingProviderId(HbmpPrincipal p) => IsProviderScoped(p) && string.IsNullOrEmpty(p.ProviderId);

    /// <summary>Authorize access to a specific provider row. Provider-scoped callers use the
    /// provider-ownership rule (tenant + PO); the tenant-scoped Network Team uses tenant-match only.</summary>
    public Task<AuthzDecision> AuthorizeAsync(HbmpPrincipal principal, string resourceTenantId, string resourceProviderId, CancellationToken ct = default)
    {
        var action = IsProviderScoped(principal) ? ProviderPolicies.Actions.ReadOwn : ProviderPolicies.Actions.Read;
        var request = new AuthzRequest(principal, action, new ResourceRef
        {
            Type = "provider",
            Id = resourceProviderId,
            TenantId = resourceTenantId,
            ProviderId = resourceProviderId,
        });
        return engine.EvaluateAsync(request, ct);
    }
}
