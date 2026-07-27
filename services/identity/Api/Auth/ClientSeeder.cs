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

        await SeedWebClientAsync(apps, ct);
        await SeedServiceClientAsync(apps, ct);
    }

    /// <summary>
    /// The SPA's public PKCE client.
    ///
    /// <para><b>RECONCILED on every start, not created once.</b> It used to be skipped whenever the client
    /// already existed, which meant every scope a later phase added to the frozen contract never reached the
    /// registered client: the row kept whatever the contract said on the day the database was first seeded.
    /// By phase 19 it was twenty scopes behind — <c>patient:read</c>, all ten claims scopes, both note
    /// scopes, and the whole policy-administration set. The symptom is not a missing feature but a REFUSED
    /// LOGIN (<c>ID2051</c>, "not allowed to use the specified scope"), because the SPA asks for the union up
    /// front; and any scope that slipped past that would have become a 403 on a screen that had rendered.</para>
    ///
    /// <para>This is the same defect 18.B1 closed for the service client (see
    /// <see cref="SeedServiceClientAsync"/>, note 3) — fixed there, left in place here.</para>
    /// </summary>
    private async Task SeedWebClientAsync(IOpenIddictApplicationManager apps, CancellationToken ct)
    {
        var webRedirect = config["Issuer:WebRedirectUri"] ?? "http://localhost:5173/";
        var postLogout = config["Issuer:WebPostLogoutUri"] ?? "http://localhost:5173/";
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
        // request the INTERACTIVE scope set — never the machine ingest/projection scopes. Reconciling widens
        // AND narrows: a scope removed from the contract is removed from the client on the next restart.
        foreach (var s in IdentityContract.InteractiveScopes) web.Permissions.Add(Permissions.Prefixes.Scope + s);

        var existing = await apps.FindByClientIdAsync(IdentityContract.WebClientId, ct);
        if (existing is null) await apps.CreateAsync(web, ct);
        else await apps.UpdateAsync(existing, web, ct);
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
