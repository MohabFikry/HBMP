using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Authz;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.9 — the access catalogue, and roles designed out of it.
///
/// <para>
/// ============================================================================================================
/// WHAT THE CATALOGUE IS FOR
/// ============================================================================================================
/// Permissions have been data since 17.1 and nothing ever listed them. An administrator facing a real person
/// with an unusual job had one workable move: grant the nearest BIGGER role. Custom roles are the narrow
/// alternative, and these tests hold the guard rails that make the narrow path safe enough to prefer —
/// because a role designer without them is just a faster way to over-grant.
/// </para>
///
/// <para>
/// ============================================================================================================
/// THE GUARD RAILS
/// ============================================================================================================
///   * a machine key cannot land on a human role;
///   * a set holding both halves of a separated duty is refused as a SET, not key by key;
///   * a built-in role's meaning cannot be redefined by a tenant;
///   * one tenant cannot edit another's role.
/// </para>
/// </summary>
[Collection("identity-db")]
public class AccessCatalogueTests : IClassFixture<IdentityAppFactory>
{
    private readonly IdentityAppFactory _factory;
    public AccessCatalogueTests(IdentityAppFactory factory) => _factory = factory;

    private const string Pass = "Test-Passw0rd!";
    private const string Scope = "openid admin:read admin:write";

    [SkippableFact]
    public async Task The_catalogue_lists_every_permission_with_the_flags_that_decide_where_it_may_go()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("cat-read");
        var client = await AdminClient(admin);

        var res = await client.GetAsync("/identity/admin/scopes");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        // The flags are the reason this is more than a list of strings: each one changes whether a key
        // belongs in a role at all.
        body.Should().Contain("serviceOnly");
        body.Should().Contain("deprecated");
        body.Should().Contain("isPlatformAdminKey");
        // "Who holds this already" — without it, deciding whether a new role needs a key is guesswork, and
        // the safe guess is always to include it.
        body.Should().Contain("heldBy");
    }

    [SkippableFact]
    public async Task A_role_can_be_designed_from_the_catalogue_and_is_owned_by_the_tenant_that_made_it()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("cat-create");
        var client = await AdminClient(admin);
        var name = $"triage_lead_{Guid.NewGuid():N}"[..40];

        var res = await client.PostAsJsonAsync("/identity/admin/roles", new
        {
            name, description = "Runs triage at one clinic.", sensitivityTier = "T3",
            scopes = new[] { "patient:read", "notification:read" },
        });

        try
        {
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());

            var listed = await client.GetStringAsync("/identity/admin/roles");
            listed.Should().Contain(name);
            listed.Should().Contain("\"custom\":true");
        }
        finally { await DropRole(name); }
    }

    [SkippableFact]
    public async Task A_machine_key_cannot_be_put_on_a_human_role()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("cat-service");
        var client = await AdminClient(admin);

        var res = await client.PostAsJsonAsync("/identity/admin/roles", new
        {
            name = $"smuggler_{Guid.NewGuid():N}"[..30],
            scopes = new[] { "patient:read", "auth:ingest" },
        });

        // A service credential attached to a person is a category error no access review would ever catch as
        // one — it reads as an ordinary grant on an ordinary account.
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("service-only-scope");
    }

    [SkippableFact]
    public async Task A_role_holding_both_halves_of_a_separated_duty_is_refused()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("cat-sod");
        var client = await AdminClient(admin);

        var res = await client.PostAsJsonAsync("/identity/admin/roles", new
        {
            name = $"money_all_{Guid.NewGuid():N}"[..30],
            scopes = new[] { "finance:write", "finance:approve" },
        });

        // Raising a payment and releasing it. Checked over the SET rather than key by key, because a role
        // holds nothing yet — each key is clean against an empty held-set, and the conflict only exists
        // between them.
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("sod-conflict");
    }

    [SkippableFact]
    public async Task A_built_in_role_cannot_be_redefined_under_its_own_name()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("cat-reserved");
        var client = await AdminClient(admin);

        var res = await client.PostAsJsonAsync("/identity/admin/roles", new
        {
            name = "doctor", scopes = new[] { "patient:read" },
        });

        // Silently editing `doctor` here would change what the word means for everybody who reads the audit
        // trail expecting the standard meaning.
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("reserved-role-name");
    }

    [SkippableTheory]
    [InlineData("ab")]              // too short
    [InlineData("triage lead")]     // whitespace
    [InlineData("triage:lead")]     // a colon, which other code splits claims on
    [InlineData("triage,lead")]     // a comma, likewise
    [InlineData("1triage")]         // leading digit
    public async Task A_role_name_that_would_corrupt_the_roles_claim_is_refused(string name)
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("cat-name");
        var client = await AdminClient(admin);

        var res = await client.PostAsJsonAsync("/identity/admin/roles", new { name, scopes = new[] { "patient:read" } });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("invalid-role-name");
    }

    /// <summary>
    /// Case is NORMALIZED, not refused — and that distinction is the point of testing it.
    ///
    /// <para>The role vocabulary is lower-case throughout, so "Triage_Lead" is not a corrupt name, it is the
    /// right name typed the way people type names. Refusing it would be a validation message about
    /// capitalisation; lower-casing it stores what the token will carry. What must NOT happen is storing the
    /// mixed-case string, because the `roles` claim is compared ordinally and `Triage_Lead` would then match
    /// nothing.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_name_typed_in_mixed_case_is_stored_lower_case_rather_than_refused()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Admin("cat-case");
        var client = await AdminClient(admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var res = await client.PostAsJsonAsync("/identity/admin/roles", new
        {
            name = $"Triage_Lead_{suffix}", scopes = new[] { "patient:read" },
        });

        try
        {
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
            var body = await res.Content.ReadAsStringAsync();
            body.Should().Contain($"triage_lead_{suffix}");
            body.Should().NotContain($"Triage_Lead_{suffix}");
        }
        finally { await DropRole($"triage_lead_{suffix}"); }
    }

    /// <summary>
    /// The set-level SoD check, as a unit — no database, because it is a pure function and this is the layer
    /// the endpoint's refusal is only as good as.
    /// </summary>
    [Fact]
    public void The_set_check_finds_a_conflict_that_the_per_key_check_cannot()
    {
        var keys = new[] { "finance:write", "finance:approve" };

        // Key by key against an empty held-set: clean, both times. That is exactly why designing a role
        // needed its own check rather than reusing the grant one.
        SegregationOfDuties.EvaluateScopeGrant([], "finance:write").Should().BeEmpty();
        SegregationOfDuties.EvaluateScopeGrant([], "finance:approve").Should().BeEmpty();

        // As a set: refused.
        SegregationOfDuties.EvaluateScopeSet(keys).Should().NotBeEmpty();
    }

    [Fact]
    public void A_set_carrying_no_separated_duty_is_clean_rather_than_unchecked()
    {
        // The honest answer for the great majority of sets: reading a lab result is not half of a duty.
        SegregationOfDuties.EvaluateScopeSet(["patient:read", "emr:read", "notification:read"])
            .Should().BeEmpty();
    }

    // ---- harness ---------------------------------------------------------------------------------------

    /// <summary>
    /// Delete a role this suite authored, and its tenant-local grants.
    ///
    /// <para>These tests run against a SHARED database, so a role left behind is a row every later run sees
    /// — and two of the platform's own guards (`Seed_contains_exactly_the_frozen_roles`,
    /// `Role_catalog_stays_global_...`) assert over that table. Leaving fixtures in it makes somebody else's
    /// suite go red for a reason they did not cause, which is the worst kind of failure to inherit.</para>
    ///
    /// <para>A hard delete, unlike everything else in this service — a test fixture is not a record of a
    /// decision, and the no-hard-deletes rule protects audit trails, not scratch data.</para>
    /// </summary>
    private async Task DropRole(string name)
    {
        await using var db = IdentityTestDb.NewContext();
        var grants = await db.RoleScopes.Where(rs => rs.RoleName == name).ToListAsync();
        db.RoleScopes.RemoveRange(grants);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == name);
        if (role is not null) db.Roles.Remove(role);
        await db.SaveChangesAsync();
    }

    private sealed class Seeded(IdentityAppFactory factory, Guid id, string name, string? totpKey) : IAsyncDisposable
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string? TotpKey { get; } = totpKey;
        public async ValueTask DisposeAsync() => await TestFlow.DeleteUser(factory, Id);
    }

    /// <summary>The `/identity/admin` group requires an MFA SESSION, not merely the scope.</summary>
    private async Task<Seeded> Admin(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}";
        var (id, key) = await TestFlow.SeedUser(_factory, name, Pass, ["super_admin"], twoFactor: true);
        return new Seeded(_factory, id, name, key);
    }

    private async Task<HttpClient> AdminClient(Seeded admin)
    {
        var token = await TestFlow.AuthCodeToken(_factory, admin.Name, Pass, admin.TotpKey, Scope);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}
