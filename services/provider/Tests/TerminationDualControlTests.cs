using FluentAssertions;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;
using ProviderEntity = Mersal.Provider.Domain.Provider;

namespace Mersal.Provider.Tests;

/// <summary>
/// Termination is dual-controlled, and the second approver has to be a person rather than a string.
///
/// <para>The endpoint advertised "dual-controlled (second approver must differ from actor)" and enforced
/// exactly that: <c>req.SecondApproverSubject != actor</c>, compared against a value the person doing the
/// terminating typed into the request body. The named approver never authenticated, never consented and was
/// never checked to exist — naming a colleague was the whole control. Termination drops a provider out of
/// the routable network, revokes every provider-scoped user's access, and publishes both facts
/// platform-wide.</para>
///
/// <para>These assert the DATABASE half of the replacement, which is the half that holds when an endpoint
/// check is bypassed by a repair script or a psql session: one open request per provider, and an approver
/// who cannot be the requester. Env-gated on <c>PROVIDER_TEST_DB_OWNER</c>.</para>
/// </summary>
public class TerminationDualControlTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_OWNER");

    private static ProviderDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ProviderDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<Guid> SeedProvider(string tenant)
    {
        await using var db = Ctx();
        var now = DateTimeOffset.UtcNow;
        var p = new ProviderEntity
        {
            ProviderId = Guid.NewGuid(), TenantId = tenant,
            ProviderCode = "PC-" + Guid.NewGuid().ToString("N")[..8], LegalName = "Test Provider",
            ProviderType = ProviderType.Clinic, Status = ProviderStatus.Active,
            OnboardingState = OnboardingState.Activated, CreatedAt = now, UpdatedAt = now,
        };
        db.Providers.Add(p);
        await db.SaveChangesAsync();
        return p.ProviderId;
    }

    private static ProviderTerminationRequest Request(string tenant, Guid providerId, string requestedBy) => new()
    {
        RequestId = Guid.NewGuid(), TenantId = tenant, ProviderId = providerId,
        Reason = "contract ended", RequestedBy = requestedBy, RequestedAt = DateTimeOffset.UtcNow,
    };

    [SkippableFact]
    public async Task The_requester_cannot_be_recorded_as_their_own_approver()
    {
        Skip.If(Owner is null, "PROVIDER_TEST_DB_OWNER not set — DB integration test skipped.");
        var tenant = T();
        var providerId = await SeedProvider(tenant);
        try
        {
            await using var db = Ctx();
            var r = Request(tenant, providerId, "user-1");
            r.Status = TerminationRequestStatus.Approved;
            r.ApprovedBy = "user-1";                       // the defect, expressed directly at the datastore
            r.ApprovedAt = DateTimeOffset.UtcNow;
            db.TerminationRequests.Add(r);

            var save = async () => await db.SaveChangesAsync();

            var thrown = await save.Should().ThrowAsync<DbUpdateException>();
            thrown.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>()
                .Which.ConstraintName.Should().Be("ck_ptr_distinct_approver",
                    "self-approval is the thing dual control exists to prevent, so the database refuses it " +
                    "rather than trusting every future caller to");
        }
        finally { await Cleanup(tenant, providerId); }
    }

    [SkippableFact]
    public async Task A_distinct_approver_is_accepted()
    {
        Skip.If(Owner is null, "PROVIDER_TEST_DB_OWNER not set — DB integration test skipped.");
        var tenant = T();
        var providerId = await SeedProvider(tenant);
        try
        {
            await using var db = Ctx();
            var r = Request(tenant, providerId, "user-1");
            r.Status = TerminationRequestStatus.Approved;
            r.ApprovedBy = "user-2";
            r.ApprovedAt = DateTimeOffset.UtcNow;
            db.TerminationRequests.Add(r);

            var save = async () => await db.SaveChangesAsync();

            await save.Should().NotThrowAsync();
        }
        finally { await Cleanup(tenant, providerId); }
    }

    [SkippableFact]
    public async Task Only_one_termination_request_may_be_open_for_a_provider()
    {
        Skip.If(Owner is null, "PROVIDER_TEST_DB_OWNER not set — DB integration test skipped.");
        var tenant = T();
        var providerId = await SeedProvider(tenant);
        try
        {
            await using (var db = Ctx())
            {
                db.TerminationRequests.Add(Request(tenant, providerId, "user-1"));
                await db.SaveChangesAsync();
            }

            await using var second = Ctx();
            // Two open requests would let two people each approve the other's and turn dual control back
            // into single control with extra steps.
            second.TerminationRequests.Add(Request(tenant, providerId, "user-2"));
            var save = async () => await second.SaveChangesAsync();

            var thrown = await save.Should().ThrowAsync<DbUpdateException>();
            thrown.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>()
                .Which.ConstraintName.Should().Be("ux_ptr_one_open_request");
        }
        finally { await Cleanup(tenant, providerId); }
    }

    [SkippableFact]
    public async Task A_settled_request_does_not_block_a_later_one()
    {
        Skip.If(Owner is null, "PROVIDER_TEST_DB_OWNER not set — DB integration test skipped.");
        var tenant = T();
        var providerId = await SeedProvider(tenant);
        try
        {
            await using (var db = Ctx())
            {
                var withdrawn = Request(tenant, providerId, "user-1");
                withdrawn.Status = TerminationRequestStatus.Withdrawn;
                withdrawn.WithdrawnAt = DateTimeOffset.UtcNow;
                db.TerminationRequests.Add(withdrawn);
                await db.SaveChangesAsync();
            }

            await using var second = Ctx();
            // The uniqueness is PARTIAL, on status='Requested'. A withdrawn attempt is history, not a lock:
            // otherwise one abandoned request would make a provider permanently unterminatable.
            second.TerminationRequests.Add(Request(tenant, providerId, "user-2"));
            var save = async () => await second.SaveChangesAsync();

            await save.Should().NotThrowAsync();
        }
        finally { await Cleanup(tenant, providerId); }
    }

    private static async Task Cleanup(string tenant, Guid providerId)
    {
        await using var db = Ctx();
        await db.TerminationRequests.Where(r => r.ProviderId == providerId).ExecuteDeleteAsync();
        await db.Providers.Where(p => p.TenantId == tenant).ExecuteDeleteAsync();
    }
}
