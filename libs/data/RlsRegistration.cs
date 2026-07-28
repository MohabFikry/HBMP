using Mersal.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Data;

/// <summary>Wiring for the shared RLS binder. A service (1) calls <see cref="AddHbmpRls"/> to register the
/// scoped <see cref="RlsContext"/> + <see cref="RlsConnectionInterceptor"/> and adds the interceptor to its
/// DbContext, then (2) calls <see cref="UseHbmpRls"/> in the pipeline to populate the context from the
/// authenticated principal before any DB work runs. Requests with no principal leave the tenant GUC empty ⇒
/// the datastore returns zero rows (fail-closed), matching the audit's default-deny requirement.</summary>
public static class RlsRegistration
{
    public static IServiceCollection AddHbmpRls(this IServiceCollection services)
    {
        services.AddScoped<RlsContext>();
        services.AddScoped<RlsConnectionInterceptor>();
        services.AddScoped<TenantStampingInterceptor>();
        return services;
    }

    /// <summary>Adds the RLS connection binder + tenant-stamping interceptor to a DbContext. Call inside
    /// <c>AddDbContext((sp,o) =&gt; o.UseNpgsql(...).AddHbmpRlsInterceptors(sp))</c>.</summary>
    public static DbContextOptionsBuilder AddHbmpRlsInterceptors(this DbContextOptionsBuilder options, IServiceProvider sp) =>
        options.AddInterceptors(
            sp.GetRequiredService<RlsConnectionInterceptor>(),
            sp.GetRequiredService<TenantStampingInterceptor>());

    /// <summary>Bind the per-request RLS GUCs from the principal. Place after UseAuthentication so the
    /// principal is resolved. Only binds under <paramref name="pathPrefix"/> (default <c>/api/v1</c>) so
    /// health/metrics requests — which carry no principal — don't need a tenant.</summary>
    public static IApplicationBuilder UseHbmpRls(this IApplicationBuilder app, string pathPrefix = "/api/v1")
    {
        return app.Use(async (ctx, next) =>
        {
            var principal = ctx.RequestServices.GetRequiredService<IHbmpPrincipalAccessor>().Principal;
            if (principal is not null && ctx.Request.Path.StartsWithSegments(pathPrefix))
            {
                var rls = ctx.RequestServices.GetRequiredService<RlsContext>();
                rls.TenantId = principal.TenantId ?? "";
                rls.ProviderId = principal.ProviderId ?? "";
                // 21.5 — the active membership, for ambient attribution. Bound in the SAME place as the
                // tenant so a service cannot end up with one and not the other, and empty for machine
                // principals, which genuinely have no membership.
                rls.MembershipId = principal.MembershipId ?? "";
            }
            await next();
        });
    }
}
