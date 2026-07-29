using Mersal.Authz;
using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mersal.Identity.Infrastructure;

/// <summary>
/// Wires the identity store: the EF <see cref="IdentityStoreDbContext"/> over Postgres, ASP.NET Core Identity
/// (user/role managers + password hashing + token providers for TOTP in 17.3), and the
/// <see cref="RoleScopeResolver"/>. The OpenIddict issuer is added in 17.2.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<IdentityStoreDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Identity")
                        ?? throw new InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention());

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                // Password policy (18-security-model). Tightened further at the issuer/login layer in 17.3.
                o.Password.RequiredLength = 12;
                o.Password.RequireNonAlphanumeric = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireDigit = true;
                o.User.RequireUniqueEmail = false; // username is the primary handle; email optional for staff
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<IdentityStoreDbContext>();
        // NOTE: .AddDefaultTokenProviders() (authenticator/TOTP + recovery codes) lands in 17.3 — it lives in
        // the ASP.NET Core framework assembly, wired at the Api/host layer alongside the login endpoints.

        services.AddScoped<RoleScopeResolver>();
        // TryAdd so a host that already registered its own clock (Api/Program.cs does) keeps it — this only
        // makes the Infrastructure package self-contained, following the libs/* precedent.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<MembershipService>();   // 21.1c — resolves the active membership (the principal)
        // 21.2 — one evaluator, two modes. Registered against both the concrete type (mode 1, token
        // issuance) and the interface (mode 2, out-of-session) so they cannot drift into two objects.
        services.AddMemoryCache();
        services.AddSingleton<DeprecationReporter>();
        services.AddScoped<SessionService>();      // 21.5 — session/device controls + login history
        services.AddScoped<EffectiveSetService>();
        services.AddScoped<IEffectiveSetService>(sp => sp.GetRequiredService<EffectiveSetService>());
        services.AddScoped<UserClaimsService>();
        // 21.4 — the issuer's projection of the programme switches, read at token issuance.
        services.AddScoped<TenantFeatureStore>();
        // DURABLE dedupe, replacing the in-memory default AddHbmpEvents registers: the question this store
        // answers is "have I EVER processed this id", which a process lifetime cannot answer.
        services.AddScoped<Mersal.Events.IProcessedEventStore, DbProcessedEventStore>();
        return services;
    }
}
