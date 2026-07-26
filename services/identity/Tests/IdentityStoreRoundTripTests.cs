using FluentAssertions;
using Mersal.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Tests;

/// <summary>The ASP.NET Identity EF store persists a user (with the Mersal ABAC extensions) through the
/// hand-authored `identity` schema — proving the DDL matches IdentityStoreDbContext. Env-gated.</summary>
[Collection("identity-db")]
public class IdentityStoreRoundTripTests
{
    [SkippableFact]
    public async Task User_round_trips_with_tenant_provider_and_display_name()
    {
        Skip.If(IdentityTestDb.Conn is null, "IDENTITY_TEST_DB not set — DB integration test skipped.");
        await using var db = IdentityTestDb.NewContext();

        var id = Guid.NewGuid();
        var uname = $"rt-{id:N}";
        var tenant = "11111111-1111-1111-1111-111111111111";
        var providerId = Guid.NewGuid();
        try
        {
            db.Users.Add(new ApplicationUser
            {
                Id = id,
                UserName = uname,
                NormalizedUserName = uname.ToUpperInvariant(),
                TenantId = tenant,
                ProviderId = providerId,
                DisplayName = "Round Trip",
                CreatedAt = DateTimeOffset.UtcNow,
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            });
            await db.SaveChangesAsync();

            await using var read = IdentityTestDb.NewContext();
            var loaded = await read.Users.AsNoTracking().SingleAsync(u => u.Id == id);
            loaded.TenantId.Should().Be(tenant);
            loaded.ProviderId.Should().Be(providerId);
            loaded.DisplayName.Should().Be("Round Trip");
            loaded.IsActive.Should().BeTrue();
        }
        finally
        {
            await using var clean = IdentityTestDb.NewContext();
            await clean.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
        }
    }
}
