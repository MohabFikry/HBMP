using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

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

/// <summary>Serializes the identity DB tests (they share the seeded `identity` schema) and hands them all the
/// one host built by <see cref="IdentityHostFixture"/>.</summary>
[Xunit.CollectionDefinition("identity-db", DisableParallelization = true)]
public sealed class IdentityDbTestGroup;
