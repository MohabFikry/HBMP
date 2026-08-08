using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Mersal.Provider.Tests;

/// <summary>
/// 25.1 — design 42 §7 rule 3: **no `provider:write` for branch roles, ever.**
///
/// `provider:write` is network-wide. It creates branches, edits external labs, pharmacies and their tariffs,
/// and it is the scope that unmasks `license_no`. A clinic coordinator who needs to maintain a doctor's
/// licence at their own branch must never acquire the authority to re-price the external network on the way.
///
/// TWO HALVES, and the second is the one that makes the first mean anything:
///   1. a coordinator's token does not satisfy the `provider:write` requirement (the real handler decides);
///   2. the endpoints in question are ACTUALLY behind that requirement.
///
/// Half 1 alone would keep passing if someone moved `POST /branches` onto a laxer policy tomorrow — the
/// coordinator would still fail a requirement nothing guards any more. Half 2 reads the route registrations
/// out of the source for that reason.
/// </summary>
public class BranchRoleCannotWriteTheNetworkTests
{
    /// <summary>The seeded branch-role scope set (identity 0021) — reception's twelve plus the four branch
    /// authorities. Written out rather than resolved from a DB so this runs everywhere; the DB-resolved copy
    /// is asserted equal to the seed by <c>BranchRoleScopeParityTests</c>.</summary>
    private static readonly string[] BranchRoleScopes =
    [
        "reception:search", "reception:read", "eligibility:check", "appointment:read", "appointment:write",
        "patient:read", "practitioner:read", "note:read", "profile:read", "callcentre:history:read",
        "notification:read", "claims:reimburse:submit",
        "branch:practitioner:write", "branch:roster:write", "branch:inventory:read", "branch:inventory:write",
    ];

    private sealed class Sink : IAuthEventSink
    {
        public void Record(AuthEvent evt) { }
    }

    private static ClaimsPrincipal Token(string role, params string[] scopes)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u-1"), new("role", role) };
        claims.AddRange(scopes.Select(s => new Claim("scope", s)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<bool> Satisfies(ClaimsPrincipal user, params string[] required)
    {
        var requirement = new ScopeRequirement(required, requireMfa: false);
        var ctx = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new ScopeAuthorizationHandler(new Sink()).HandleAsync(ctx);
        return ctx.HasSucceeded;
    }

    [Theory]
    [InlineData("branch_coordinator")]
    [InlineData("clinics_manager")]
    public async Task A_branch_role_cannot_satisfy_provider_write(string role)
    {
        var user = Token(role, BranchRoleScopes);

        (await Satisfies(user, "provider:write")).Should().BeFalse(
            "'{0}' must never reach the network-wide write surface — POST /branches, external provider and " +
            "tariff edits (design 42 §7 rule 3)", role);
        (await Satisfies(user, "provider:admin")).Should().BeFalse();
    }

    [Fact]
    public async Task AND_THE_NEGATION_the_network_team_does_satisfy_it()
    {
        // Without this, a broken harness that denies everything would report the refusal above as a success.
        (await Satisfies(Token("network_team", "provider:read", "provider:write"), "provider:write"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("branch_coordinator")]
    [InlineData("clinics_manager")]
    public async Task A_branch_role_DOES_satisfy_its_own_branch_scoped_authorities(string role)
    {
        // The other direction: the scopes exist to be usable. If these were unreachable the phase would have
        // built a role that can do nothing, which is the failure 19.7 found three times over.
        var user = Token(role, BranchRoleScopes);

        (await Satisfies(user, "provider:write", "branch:practitioner:write")).Should().BeTrue(
            "the widened practitioner-write group (25.2) must admit the branch scope");
        (await Satisfies(user, "branch:roster:write")).Should().BeTrue();
        (await Satisfies(user, "branch:inventory:read")).Should().BeTrue();
        (await Satisfies(user, "branch:inventory:write")).Should().BeTrue();
    }

    [Fact]
    public void The_branch_registry_write_group_really_is_behind_provider_write()
    {
        // Half 2. `POST /branches` is "create a clinic" — a coordinator runs one, they do not create one.
        var src = Source("Api", "Branches.cs");

        src.Should().MatchRegex(
            @"var write = app\.MapGroup\(""/api/v1/branches""\)\s*\.RequireAuthorization\(HbmpPolicies\.Scope\(""provider:write""\)\)",
            "the branch write group must require provider:write — if this moved, the refusal test above " +
            "stopped protecting anything");
    }

    [Fact]
    public void The_external_provider_write_surface_really_is_behind_provider_write()
    {
        // The external network — providers, contracts, locations, tariffs — is registered in Program.cs and
        // Onboarding.cs, and network TIERS sit one step higher again on provider:admin (ADR-0019: pricing AT a
        // tier and deciding WHICH tier are different authorities).
        foreach (var file in new[] { "Program.cs", "Onboarding.cs" })
        {
            var src = Source("Api", file);
            Regex.IsMatch(src, @"RequireAuthorization\(HbmpPolicies\.Scope\(""provider:write""\)\)")
                .Should().BeTrue("{0} must gate the external-provider write group on provider:write", file);

            // And it must NOT have quietly acquired a branch scope as an alternative. Widening the
            // PRACTITIONER group (25.2) is deliberate and confined to practitioners; widening the external
            // network surface the same way would hand a clinic coordinator the lab and pharmacy directory.
            //
            // Matched on the AUTHORIZATION REGISTRATION rather than on the text "branch:" anywhere in the
            // file. The crude substring version failed the moment Program.cs gained a comment explaining the
            // branch middleware — a guard that fires on prose is a guard people learn to edit around.
            foreach (Match m in Regex.Matches(src, @"RequireAuthorization\((?<args>[^;]*?)\)\s*;"))
                m.Groups["args"].Value.Should().NotContain("branch:",
                    "{0} is network administration and must stay on provider:write alone", file);
        }

        Source("Api", "NetworkTiers.cs").Should().Contain(@"HbmpPolicies.Scope(""provider:admin"")",
            "network tier administration stays above provider:write, let alone a branch scope");
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "services", "provider", .. parts]));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
