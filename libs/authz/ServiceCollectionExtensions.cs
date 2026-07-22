using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Authz;

/// <summary>
/// Wires the mandatory authorization engine into a service. Uses the native default-deny evaluator over
/// the supplied (or default) policy bundle; a Cerbos/OPA sidecar can replace <see cref="IAuthorizationEngine"/>
/// later without touching callers (ADR-0005). Requires libs/audit-client to be registered (for deny audit).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHbmpAuthorization(
        this IServiceCollection services,
        PolicyBundle? bundle = null,
        FieldAccessMatrix? fieldMatrix = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBreakGlassProvider>(NullBreakGlassProvider.Instance);
        services.TryAddSingleton(bundle ?? DefaultPolicies.Bundle());
        services.TryAddSingleton(fieldMatrix ?? DefaultPolicies.FieldMatrix());
        services.TryAddScoped<IAuthorizationEngine, DefaultAuthorizationEngine>();
        services.TryAddScoped<FieldProjector>();
        return services;
    }
}
