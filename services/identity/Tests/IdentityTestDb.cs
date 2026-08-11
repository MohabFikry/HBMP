using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

/// <summary>
/// Deletes the fixtures this suite mints, whether or not the run that minted them finished.
///
/// <para>
/// ============================================================================================================
/// WHY A SWEEP EXISTS WHEN EVERY TEST ALREADY CLEANS UP
/// ============================================================================================================
/// Every test here does the right thing: a <c>try/finally</c> around the body, or an <c>await using</c> on a
/// disposable seed. Those are correct and they are not enough, because none of them is reachable when the
/// PROCESS does not get to run them — a cancelled <c>dotnet test</c>, a Ctrl-C, a debugger stopped mid-test,
/// a host that fails to boot, an OOM kill. Seven accounts were found in the shared development database this
/// way: no sessions, no login attempts, nothing to indicate the tests that own them had done anything at all
/// before the run died. `finally` had simply never executed.
/// </para>
///
/// <para>
/// Against a database only the tests touch, that would be untidy. This one is the DEVELOPMENT database, so a
/// leaked fixture is a row that shows up in the admin console beside real staff, and a leaked ROLE is worse
/// still: <c>RoleScopeMatrixTests.Seed_contains_exactly_the_frozen_roles</c> and
/// <c>TenantLocalRoleScopeTests</c> both assert over that table, so somebody else's suite goes red for a
/// reason they did not cause.
/// </para>
///
/// <para>
/// ============================================================================================================
/// IT RUNS AT BOTH ENDS, AND THE START IS THE IMPORTANT ONE
/// ============================================================================================================
/// Disposal catches this run's leaks whenever the process exits normally — including when tests FAIL, which
/// is the common case. It cannot catch a kill, because a killed process disposes nothing. Construction is
/// what covers that: the next run starts by clearing whatever the last one left, so the window in which a
/// stray fixture is visible closes without anybody having to remember. Neither half is redundant.
/// </para>
///
/// <para>
/// ============================================================================================================
/// WHAT IT WILL AND WILL NOT MATCH
/// ============================================================================================================
/// Accounts are matched on the <c>@example.org</c> address <see cref="TestFlow.SeedUser"/> mints. That domain
/// is reserved by RFC 2606 for exactly this and can never belong to a real person, and the demo seeder uses
/// <c>@mersal.local</c> — so the predicate cannot have a false positive, which is the only property that
/// matters in a routine that deletes rows nobody asked it to.
/// </para>
///
/// <para>
/// Roles are narrower and deliberately conservative, because the Access Catalogue's role designer now lets a
/// real administrator create tenant-owned roles against this same database. Being tenant-owned is therefore
/// NOT sufficient. A role must also end in a run of at least eight hex characters — the tail of the
/// <c>Guid.NewGuid():N</c> suffix every test here appends — which a role somebody named by hand will not
/// have. A hand-made role that did would survive as far as the next assertion about the frozen role set; a
/// hand-made role wrongly deleted is somebody's afternoon.
/// </para>
/// </summary>
internal static class TestFixtureSweep
{
    /// <summary>RFC 2606 reserved. No real account can hold one, which is what makes this safe to delete on.</summary>
    private const string TestEmailDomain = "@example.org";

    /// <summary>
    /// The tail of a `Guid.NewGuid():N` suffix, after the tests' `[..30]`-style truncation has taken a bite
    /// out of it. Eight is the shortest any of them leaves behind.
    /// </summary>
    private const string MintedRoleSuffix = "_[0-9a-f]{8,}$";

    /// <summary>
    /// Delete every fixture account and every test-minted custom role, and report what went.
    ///
    /// <para>Raw SQL rather than EF: this runs before any host is built and must not depend on one, and the
    /// whole dependent subtree — memberships, membership roles and overrides, sessions, tokens, claims,
    /// logins — is `ON DELETE CASCADE` from the account, so one statement is the complete operation.
    /// `login_attempt` is `ON DELETE SET NULL` by design, so the attack history outlives the account it was
    /// against; sweeping the account correctly leaves an anonymous attempt row behind.</para>
    ///
    /// <para>
    /// ============================================================================================================
    /// IT DOES NOT SWALLOW ITS OWN FAILURES
    /// ============================================================================================================
    /// This was written with a <c>try/catch</c> around it, on the reasoning that housekeeping should not turn a
    /// tidiness problem into a red build. It then failed on its very first run — the role pattern's <c>{8,}</c>
    /// collided with the <c>{0}</c> placeholder syntax <c>ExecuteSqlRaw</c> parses — and the catch turned that
    /// into silence: nothing deleted, nothing reported, a sweep that looked like it had run. Which is the exact
    /// failure mode this whole audit was about, committed by the code cleaning up after it.
    /// </para>
    ///
    /// <para>So it throws. The cost is honest and small: this only runs when <c>IDENTITY_TEST_DB</c> is set,
    /// and a database that cannot answer a DELETE is one where every test in the collection is failing
    /// anyway — the sweep is not adding fragility, it is declining to hide it.</para>
    /// </summary>
    public static void Run(string when)
    {
        if (IdentityTestDb.Conn is null) return;   // DB-less run: nothing was created, nothing to remove.

        using var db = IdentityTestDb.NewContext();

        // Both patterns travel as PARAMETERS, never interpolated into the SQL. `ExecuteSqlRaw` scans its sql
        // argument for `{n}` placeholders, and `_[0-9a-f]{8,}$` contains a brace group that is a regex
        // quantifier to Postgres and a malformed placeholder to EF. As a parameter it is opaque to both.
        //
        // `role_scope` has no foreign key to `role` — it is keyed by NAME (tenant, role_name, scope_name) —
        // so a cascade cannot reach it and it has to be deleted explicitly, before the role it points at.
        var scopes = db.Database.ExecuteSqlRaw(
            """
            DELETE FROM identity.role_scope
             WHERE role_name IN (SELECT name FROM identity.role
                                  WHERE owner_tenant_id IS NOT NULL AND name ~ {0})
            """, MintedRoleSuffix);
        var roles = db.Database.ExecuteSqlRaw(
            "DELETE FROM identity.role WHERE owner_tenant_id IS NOT NULL AND name ~ {0}", MintedRoleSuffix);
        var accounts = db.Database.ExecuteSqlRaw(
            """DELETE FROM identity."user" WHERE email LIKE {0}""", $"%{TestEmailDomain}");

        // Announced rather than silent. A routine that quietly deletes rows is one nobody can debug when it
        // deletes the wrong ones, and the counts are the only evidence a leak happened at all.
        if (accounts + roles + scopes > 0)
            Console.Error.WriteLine(
                $"[identity-tests] swept {when}: {accounts} fixture account(s), {roles} role(s), {scopes} role-scope grant(s)");
    }
}

/// <summary>Shared helpers for the env-gated identity DB tests. IDENTITY_TEST_DB is a connection string to a
/// Postgres already migrated by tools/ci/apply-migrations.sh (schema `identity`). DB-less CI skips.</summary>
internal static class IdentityTestDb
{
    public static readonly string? Conn = Environment.GetEnvironmentVariable("IDENTITY_TEST_DB");

    public static IdentityStoreDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<IdentityStoreDbContext>()
            .UseNpgsql(Conn!)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IdentityStoreDbContext(options);
    }
}

/// <summary>
/// ONE identity host for the whole collection.
///
/// <para>Every DB-gated identity test used to open its own <see cref="IdentityAppFactory"/>, and booting the
/// issuer is expensive — it stands up ASP.NET Identity, OpenIddict and the client/scope seeders, ~20 seconds
/// each time. At 40 call sites that was 18 minutes, about 95% of the entire solution's test runtime, to
/// rebuild the same host 40 times over.</para>
///
/// <para>Sharing is safe precisely because this collection already sets <c>DisableParallelization</c>: the
/// tests run one at a time, so there is no concurrent access to serialise. They were never isolated by HOST
/// anyway — they isolate by DATA, each seeding a uniquely-named user and deleting it in a finally.</para>
///
/// <para><b>Built lazily, and that is load-bearing.</b> With IDENTITY_TEST_DB unset every test answers
/// <c>Skip.If</c> before touching this property, so no host is built and the DB-less run stays green.
/// Constructing it eagerly would boot an issuer with no database and fail the whole collection on exactly the
/// machines that are meant to skip it.</para>
/// </summary>
public sealed class IdentityHostFixture : IDisposable
{
    private IdentityAppFactory? _factory;

    public IdentityAppFactory Factory => _factory ??= new IdentityAppFactory();

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}

/// <summary>
/// Runs <see cref="TestFixtureSweep"/> once before the collection and once after it.
///
/// <para><b>A COLLECTION fixture, not a class one, and that is the whole point.</b> The obvious place for
/// this was <see cref="IdentityHostFixture"/> — but that is attached with <c>IClassFixture</c>, so xUnit
/// builds and disposes it once per TEST CLASS. The sweep would have run twenty times, "before the run" would
/// have meant "before this class", and six classes that take <see cref="IdentityAppFactory"/> directly would
/// never have triggered it at all. A collection fixture is constructed once before the first test in
/// <c>identity-db</c> and disposed once after the last, which is the lifetime the words actually describe.</para>
///
/// <para>Its own type rather than folded into the host fixture, so it stays true if the host wiring ever
/// changes again.</para>
/// </summary>
public sealed class IdentityFixtureSweep : IDisposable
{
    public IdentityFixtureSweep() => TestFixtureSweep.Run("before the run");

    // Reached whenever the process exits normally — INCLUDING when tests failed, which is precisely when a
    // test's own `finally` is most likely to have been stepped over.
    public void Dispose() => TestFixtureSweep.Run("after the run");
}

/// <summary>Serializes the identity DB tests (they share the seeded `identity` schema) and hands them all the
/// one host built by <see cref="IdentityHostFixture"/>.</summary>
/// <remarks>The <see cref="IdentityFixtureSweep"/> collection fixture brackets the whole collection, deleting
/// leftover fixtures before the first test and after the last — the backstop for the runs that never reach
/// their own `finally`.</remarks>
[Xunit.CollectionDefinition("identity-db", DisableParallelization = true)]
public sealed class IdentityDbTestGroup : Xunit.ICollectionFixture<IdentityFixtureSweep>;
