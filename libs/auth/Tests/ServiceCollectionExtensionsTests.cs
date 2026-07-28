using FluentAssertions;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Auth.Tests;

public class ServiceCollectionExtensionsTests
{
    private static IServiceProvider Build(bool requireMfa = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = "https://identity.local",
            ["Auth:Audience"] = "hbmp-api",
            ["Auth:RequireHttpsMetadata"] = "false",
            ["Auth:ProtectedScopeRequiresMfa"] = requireMfa ? "true" : "false",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHbmpAuthentication(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Missing_authority_throws()
    {
        var services = new ServiceCollection();
        var act = () => services.AddHbmpAuthentication(new HbmpAuthOptions { Authority = "" });
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Scope_policy_is_generated_on_demand_with_mfa_when_configured()
    {
        var provider = Build(requireMfa: true).GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(HbmpPolicies.Scope("orders:consume"));

        policy.Should().NotBeNull();
        var scopeReq = policy!.Requirements.OfType<ScopeRequirement>().Single();
        scopeReq.Scope.Should().Be("orders:consume");
        scopeReq.RequireMfa.Should().BeTrue();
    }

    [Fact]
    public async Task Scope_policy_omits_mfa_when_disabled()
    {
        var provider = Build(requireMfa: false).GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(HbmpPolicies.Scope("reception:read"));

        policy!.Requirements.OfType<ScopeRequirement>().Single().RequireMfa.Should().BeFalse();
    }

    [Fact]
    public async Task Mfa_policy_is_available()
    {
        var provider = Build().GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(HbmpPolicies.Mfa);

        policy!.Requirements.OfType<MfaRequirement>().Should().ContainSingle();
    }

    [Fact]
    public void Default_auth_event_sink_is_the_null_stub_until_audit_client()
    {
        Build().GetRequiredService<IAuthEventSink>().Should().BeOfType<NullAuthEventSink>();
    }

    [Fact]
    public void An_issuer_is_accepted_with_and_without_its_trailing_slash()
    {
        // The whole platform rejected every browser-issued token over this. OpenIddict publishes its issuer
        // as `http://host/`; every service was configured `http://host`; string equality said no, and the
        // 401 named the issuer it had just refused — which reads as though the ISSUER is misconfigured
        // rather than the list that failed to match it.
        //
        // Asserted on the wired handler rather than a helper, because the value that matters is the one the
        // JwtBearer options actually carry.
        var options = new HbmpAuthOptions
        {
            Authority = "https://identity.local",
            Audience = "hbmp-api",
            ValidIssuers = ["http://localhost:8090/"],
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHbmpAuthentication(options);

        var jwt = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<
                Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);

        jwt.TokenValidationParameters.ValidIssuers.Should().Contain([
            "https://identity.local", "https://identity.local/",
            "http://localhost:8090", "http://localhost:8090/",
        ]);
        // Still a closed list — normalising the slash is not the same as accepting anyone.
        jwt.TokenValidationParameters.ValidIssuers.Should().NotContain("https://evil.example");
    }
}
