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

/// <summary>Serializes the identity DB tests (they share the seeded `identity` schema).</summary>
[Xunit.CollectionDefinition("identity-db", DisableParallelization = true)]
public sealed class IdentityDbTestGroup;
