using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Identity;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Seeds the demo staff accounts the retired Keycloak realm used to provide (Phase 17.6 cutover), so the live
/// role portals keep working: one user per frozen role, username = the role name, a shared dev password. Runs
/// only when <c>Issuer:SeedDemoUsers</c> is enabled (default: Development). Idempotent. NEVER enable in a real
/// environment — production users are provisioned through the 17.4 admin surface.
/// </summary>
public sealed class UserSeeder(IServiceProvider services, IConfiguration config, IWebHostEnvironment env,
    TimeProvider clock, ILogger<UserSeeder> log)
    : IHostedService
{
    private const string DemoTenant = "11111111-1111-1111-1111-111111111111";

    public async Task StartAsync(CancellationToken ct)
    {
        var enabled = config.GetValue<bool?>("Issuer:SeedDemoUsers") ?? env.IsDevelopment();
        if (!enabled) return;

        // 18.B1: the shared demo password was a literal in this file — a real, working credential for
        // seventeen accounts (one per role), committed and greppable. It comes from configuration now, with
        // NO default: seeding without one is a startup failure rather than a silent well-known password.
        var demoPassword = config["Issuer:DemoPassword"];
        if (string.IsNullOrWhiteSpace(demoPassword))
            throw new InvalidOperationException(
                "Issuer:SeedDemoUsers is enabled but Issuer:DemoPassword is not set. Demo accounts exist for " +
                "every role; their password must be supplied by the environment, never baked into the image.");
        if (!env.IsDevelopment())
            log.LogWarning("SEEDING DEMO USERS OUTSIDE DEVELOPMENT — one account per role with a shared password");

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApplicationUser>>();

        foreach (var role in IdentityContract.Roles)
        {
            if (await users.FindByNameAsync(role) is not null) continue;
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(), UserName = role, Email = $"{role}@mersal.local",
                EmailConfirmed = true, TenantId = DemoTenant, DisplayName = Title(role),
                CreatedAt = clock.GetUtcNow(), IsActive = true,
            };
            // Set the hash directly so the documented demo password is used regardless of the admin policy.
            user.PasswordHash = hasher.HashPassword(user, demoPassword);
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                log.LogWarning("demo user {Role} not seeded: {Errors}", role, string.Join("; ", created.Errors.Select(e => e.Description)));
                continue;
            }
            await users.AddToRoleAsync(user, role);
        }
        log.LogInformation("Demo staff accounts ensured (username = role, shared dev password).");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private static string Title(string role) =>
        string.Join(' ', role.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
