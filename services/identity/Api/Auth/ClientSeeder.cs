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
public sealed class ClientSeeder(IServiceProvider services, IConfiguration config) : IHostedService
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
            foreach (var s in IdentityContract.Scopes) web.Permissions.Add(Permissions.Prefixes.Scope + s);
            await apps.CreateAsync(web, ct);
        }

        var serviceSecret = config["Issuer:ServiceClientSecret"] ?? "dev-service-secret-change-me";
        if (await apps.FindByClientIdAsync(IdentityContract.ServiceClientId, ct) is null)
        {
            var svc = new OpenIddictApplicationDescriptor
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
            // Service clients may request the machine-oriented scopes (ingest, projections, reads).
            foreach (var s in IdentityContract.Scopes) svc.Permissions.Add(Permissions.Prefixes.Scope + s);
            await apps.CreateAsync(svc, ct);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
