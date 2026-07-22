using System.Security.Claims;
using FluentAssertions;
using Mersal.Auth;

namespace Mersal.Auth.Tests;

public class HbmpPrincipalTests
{
    private static ClaimsPrincipal User(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    [Fact]
    public void FromClaims_extracts_subject_tenant_provider_session()
    {
        var user = User(
            new Claim("sub", "user-123"),
            new Claim("tenant_id", "tenant-0"),
            new Claim("provider_id", "prov-9"),
            new Claim("sid", "sess-abc"),
            new Claim("src_ip", "10.0.0.5"));

        var p = HbmpPrincipal.FromClaims(user);

        p.Subject.Should().Be("user-123");
        p.TenantId.Should().Be("tenant-0");
        p.ProviderId.Should().Be("prov-9");
        p.SessionId.Should().Be("sess-abc");
        p.SourceIp.Should().Be("10.0.0.5");
    }

    [Fact]
    public void FromClaims_without_subject_throws()
    {
        var act = () => HbmpPrincipal.FromClaims(User(new Claim("tenant_id", "t")));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ExtractScopes_splits_space_delimited_scope_claim()
    {
        var user = User(new Claim("sub", "u"), new Claim("scope", "orders:consume auth:decide reception:read"));

        var p = HbmpPrincipal.FromClaims(user);

        p.Scopes.Should().BeEquivalentTo("orders:consume", "auth:decide", "reception:read");
        p.HasScope("auth:decide").Should().BeTrue();
        p.HasScope("pharmacy:dispense").Should().BeFalse();
    }

    [Fact]
    public void ExtractRoles_reads_keycloak_realm_access_and_resource_access()
    {
        var user = User(
            new Claim("sub", "u"),
            new Claim("realm_access", """{"roles":["Doctor","reception"]}"""),
            new Claim("resource_access", """{"hbmp-api":{"roles":["orders-consumer"]}}"""));

        var p = HbmpPrincipal.FromClaims(user);

        p.Roles.Should().Contain(new[] { "doctor", "reception", "orders-consumer" });
        p.IsInRole("Doctor").Should().BeTrue(); // case-insensitive
    }

    [Fact]
    public void Malformed_realm_access_json_is_ignored_not_thrown()
    {
        var user = User(new Claim("sub", "u"), new Claim("realm_access", "{not-json"));
        var p = HbmpPrincipal.FromClaims(user);
        p.Roles.Should().BeEmpty();
    }
}
