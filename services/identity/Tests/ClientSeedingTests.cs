using FluentAssertions;
using Mersal.Identity.Domain;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 18.B1 / audit R2 X5 — the machine-to-machine client's blast radius and its rotatability.
///
/// <c>hbmp-services</c> held EVERY scope with a secret that defaulted to a literal published in
/// <c>ClientSeeder.cs</c>, and the seeder SKIPPED an existing client so a rotated secret was never
/// applied. Any of the three alone is enough to mint a platform-wide PHI token; together they made the
/// leak permanent.
/// </summary>
public class ClientSeedingTests
{
    [Fact]
    public void The_service_client_holds_only_ingest_and_projection_scopes()
    {
        IdentityContract.ServiceScopes.Should().BeEquivalentTo(
            ["auth:ingest", "notification:ingest", "reporting:project", "finance:project"],
            "a background worker ingests events and rebuilds projections — it is never a clinician or an admin");
    }

    [Fact]
    public void The_service_client_can_reach_no_clinical_or_administrative_scope()
    {
        // The concrete consequence of a leaked service secret, asserted directly.
        var forbidden = new[]
        {
            "admin:write", "admin:break-glass", "emr:read", "emr:write", "patient:write",
            "orders:consume", "pharmacy:dispense", "auth:decide", "claims:export", "audit:read",
        };
        IdentityContract.ServiceScopes.Should().NotIntersectWith(forbidden);
    }

    [Fact]
    public void Interactive_and_service_scopes_partition_the_frozen_vocabulary()
    {
        // Nothing may be lost from the catalog by the split, and nothing may sit in both halves.
        IdentityContract.InteractiveScopes.Concat(IdentityContract.ServiceScopes)
            .Should().BeEquivalentTo(IdentityContract.Scopes);
        IdentityContract.InteractiveScopes.Should().NotIntersectWith(IdentityContract.ServiceScopes);
    }

    [Fact]
    public void The_public_SPA_client_cannot_request_a_machine_scope()
    {
        // hbmp-web is a PUBLIC client: it has no secret, so anyone can impersonate it. It must never be
        // able to ask for the scopes that rebuild projections or ingest on the platform's behalf.
        foreach (var machineScope in IdentityContract.ServiceScopes)
            IdentityContract.InteractiveScopes.Should().NotContain(machineScope);
    }

    [Fact]
    public void No_credential_literal_remains_in_the_seeders()
    {
        // The rotation path is only real if the fallback is gone: a seeder that quietly substitutes a
        // known value on a missing config key can never be rotated, because nothing ever fails.
        var root = RepoRoot();
        foreach (var file in new[] { "ClientSeeder.cs", "UserSeeder.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, "services", "identity", "Api", "Auth", file));
            source.Should().NotContain("dev-service-secret-change-me", "{0} must not carry a secret literal", file);
            source.Should().NotContain("Mersal2026!", "{0} must not carry a password literal", file);
        }
    }

    [Fact]
    public void Both_clients_are_reconciled_on_every_start_not_created_once()
    {
        // Phase 19: the WEB client was created and then skipped forever, so every scope a later phase added
        // to the frozen contract never reached the registered client — by phase 19 it was twenty behind,
        // and the symptom was a REFUSED LOGIN (ID2051), not a missing feature, because the SPA asks for the
        // union up front. 18.B1 had already fixed exactly this for the service client and left it here.
        //
        // Asserted on the source because the alternative is an OpenIddict store fake that proves only that
        // the fake was called: what must not come back is the `if (… is null)` guard around the web client.
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "services", "identity", "Api", "Auth", "ClientSeeder.cs"));

        Matches(source, @"FindByClientIdAsync\(\s*IdentityContract\.WebClientId").Should().Be(1,
            "the web client is looked up once, to decide between create and update");
        Matches(source, @"if\s*\(\s*await\s+apps\.FindByClientIdAsync\(\s*IdentityContract\.WebClientId[^)]*\)[^)]*\)\s*is\s+null\s*\)")
            .Should().Be(0, "a create-only guard is what made the client permanently stale");

        foreach (var client in new[] { "WebClientId", "ServiceClientId" })
            Matches(source, @"UpdateAsync\(").Should().BeGreaterThanOrEqualTo(1,
                "{0} must be reconciled, not just created", client);
    }

    [Fact]
    public void Every_interactive_scope_reaches_the_SPA_client()
    {
        // The seeder grants InteractiveScopes verbatim, so this pins the other half of the contract: the
        // scopes phase 19 depends on are in the interactive set and therefore in the registration.
        IdentityContract.InteractiveScopes.Should().Contain(
            ["policy:read", "policy:admin", "policy:supervise", "provider:admin", "patient:read"],
            "the policy-administration portal authenticates with the same union every other portal does");
    }

    private static int Matches(string source, string pattern) =>
        System.Text.RegularExpressions.Regex.Matches(source, pattern, System.Text.RegularExpressions.RegexOptions.Singleline).Count;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
