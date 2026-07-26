using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddScoped<UserClaimsService>();
        return services;
    }
}
