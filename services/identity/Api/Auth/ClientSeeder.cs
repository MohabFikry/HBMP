using Mersal.Identity.Domain;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Idempotently registers the OAuth clients + scope descriptors the platform needs (17.2): the SPA public
/// PKCE client (<c>hbmp-web</c>), a confidential service-to-service client (<c>hbmp-services</c>,
/// client-credentials), and every frozen scope. Redirect/origin URIs come from config so the deployed tiers
/// override the dev defaults. Runs at startup; safe to re-run.
/// </summary>
public sealed class ClientSeeder(IServiceProvider services, IConfiguration config, IWebHostEnvironment env) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

        foreach (var name in IdentityContract.Scopes)
        {
            if (await scopes.FindByNameAsync(name, ct) is not null) continue;
            await scopes.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = name,
                Resources = { IdentityContract.ApiResource },
            }, ct);
        }

        var webRedirect = config["Issuer:WebRedirectUri"] ?? "http://localhost:5173/";
        var postLogout = config["Issuer:WebPostLogoutUri"] ?? "http://localhost:5173/";
        if (await apps.FindByClientIdAsync(IdentityContract.WebClientId, ct) is null)
        {
            var web = new OpenIddictApplicationDescriptor
            {
                ClientId = IdentityContract.WebClientId,
                ClientType = ClientTypes.Public,
                ConsentType = ConsentTypes.Implicit,
                DisplayName = "Mersal Web (SPA)",
                RedirectUris = { new Uri(webRedirect) },
                PostLogoutRedirectUris = { new Uri(postLogout) },
                Permissions =
                {
                    Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                    Permissions.Endpoints.Logout,
                    Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Profile, Permissions.Scopes.Email,
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            };
            // 18.B1: the SPA is a PUBLIC client (no secret, anyone can impersonate it), so it may only ever
            // request the INTERACTIVE scope set — never the machine ingest/projection scopes.
            foreach (var s in IdentityContract.InteractiveScopes) web.Permissions.Add(Permissions.Prefixes.Scope + s);
            await apps.CreateAsync(web, ct);
        }

        await SeedServiceClientAsync(apps, ct);
    }

    /// <summary>
    /// 18.B1 (audit R2 X5) — the machine-to-machine client. Three defects, all closed here:
    ///
    /// (1) The secret fell back to a hard-coded development literal published in this very file, so
    ///     anyone reaching <c>/connect/token</c> minted a platform-wide PHI token. Outside Development a
    ///     missing secret is now a STARTUP FAILURE, not a known default.
    /// (2) It held EVERY scope, so one leaked secret reached every beneficiary record. It is now limited
    ///     to <see cref="IdentityContract.ServiceScopes"/> — ingest and projections only.
    /// (3) Seeding was SKIPPED whenever the client already existed, so a rotated secret in config was
    ///     never applied and the compromised one kept working forever. The descriptor is now RECONCILED
    ///     on every start, which is what makes rotation a restart rather than a manual DB edit.
    /// </summary>
    private async Task SeedServiceClientAsync(IOpenIddictApplicationManager apps, CancellationToken ct)
    {
        var serviceSecret = config["Issuer:ServiceClientSecret"];
        if (string.IsNullOrWhiteSpace(serviceSecret))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException(
                    "Issuer:ServiceClientSecret is not configured. The service-to-service client mints tokens " +
                    "that reach PHI; it must never fall back to a baked-in default. Inject it via environment " +
                    "or OpenBao.");
            serviceSecret = "dev-only-" + Guid.NewGuid().ToString("N");   // random per run, never a known value
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = IdentityContract.ServiceClientId,
            ClientSecret = serviceSecret,
            ClientType = ClientTypes.Confidential,
            DisplayName = "Mersal service-to-service",
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
            },
        };
        foreach (var s in IdentityContract.ServiceScopes) descriptor.Permissions.Add(Permissions.Prefixes.Scope + s);

        var existing = await apps.FindByClientIdAsync(IdentityContract.ServiceClientId, ct);
        if (existing is null) await apps.CreateAsync(descriptor, ct);
        else await apps.UpdateAsync(existing, descriptor, ct);   // rotation + scope narrowing take effect on restart
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
