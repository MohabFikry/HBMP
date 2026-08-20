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
                // ========================================================================================
                // 28.8 — UNIQUENESS IS ENFORCED, BUT NOT THROUGH THIS FLAG.
                // ========================================================================================
                // An email address is now a SIGN-IN CREDENTIAL (`SessionApiEndpoints.ResolveLoginAsync`), so
                // it must identify exactly one account: two staff sharing a departmental mailbox would mean
                // `FindByEmailAsync` picking one of them, and which one it picked would decide whose
                // password was checked. So uniqueness is enforced in two places that can actually hold it:
                //
                //   * a UNIQUE INDEX on `normalized_email` (migration 0035) — the only layer without a race,
                //     since Identity's own check is a read-then-write and two administrators creating the
                //     same address at the same moment would both pass it;
                //   * an explicit `FindByEmailAsync` check in the create and update endpoints, which exists
                //     to produce a 409 with a sentence in it rather than a constraint violation.
                //
                // ========================================================================================
                // WHY THE FLAG ITSELF STAYS OFF
                // ========================================================================================
                // `RequireUniqueEmail` conflates UNIQUE with REQUIRED: with it on, `UserValidator` rejects
                // any user whose email is empty — on every `UpdateAsync`, not just creation. Accounts that
                // predate 28.8 legitimately have none (service accounts, seeded fixtures), so turning it on
                // made correcting such an account's DISPLAY NAME fail with "Email is invalid", and made
                // clearing an address impossible. Requiredness belongs at the create endpoint, where it is
                // checked explicitly and the message says what is actually wrong.
                o.User.RequireUniqueEmail = false;
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
