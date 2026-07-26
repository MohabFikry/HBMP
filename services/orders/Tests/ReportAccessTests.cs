using FluentAssertions;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>Phase 14.7 — sensitive-result gating + the release-request workflow (design 37 §6). Pure gate
/// matrix always runs; the grant-lifecycle checks are env-gated by <c>ORDERS_TEST_DB</c>. Proves: only the
/// author (or an active-grant holder) sees full content, the approvals team gets existence-only; grants are
/// single-result, non-transferable, time-boxed, revocable and auto-expiring.</summary>
public class ReportAccessTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("ORDERS_TEST_DB");
    private static OrdersDbContext Ctx() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    // ---- pure gate matrix (design 37 §6) --------------------------------------------------------
    [Fact]
    public void Standard_results_are_full_for_everyone()
    {
        SensitiveResultGate.Decide(SensitivityLevel.Standard, isAuthor: false, hasActiveGrant: false)
            .Should().Be(ResultDisclosure.Full);
    }

    [Fact]
    public void Sensitive_results_are_full_only_for_the_author_or_a_grant_holder_else_existence_only()
    {
        SensitiveResultGate.Decide(SensitivityLevel.Sensitive, isAuthor: true, hasActiveGrant: false).Should().Be(ResultDisclosure.Full);
        SensitiveResultGate.Decide(SensitivityLevel.Sensitive, isAuthor: false, hasActiveGrant: true).Should().Be(ResultDisclosure.Full);
        SensitiveResultGate.Decide(SensitivityLevel.Sensitive, isAuthor: false, hasActiveGrant: false).Should().Be(ResultDisclosure.ExistenceOnly);
        SensitiveResultGate.Decide(SensitivityLevel.HighlySensitive, isAuthor: false, hasActiveGrant: false).Should().Be(ResultDisclosure.ExistenceOnly);
    }

    [Fact]
    public void Only_the_author_or_a_medical_director_may_decide()
    {
        SensitiveResultGate.CanDecide(isAuthor: true, new HashSet<string>()).Should().BeTrue();
        SensitiveResultGate.CanDecide(isAuthor: false, new HashSet<string> { "medical_director" }).Should().BeTrue();
        SensitiveResultGate.CanDecide(isAuthor: false, new HashSet<string> { "medical_approval" }).Should().BeFalse();
    }

    [Fact]
    public void Grant_ttl_defaults_are_shorter_for_highly_sensitive()
    {
        SensitiveResultGate.DefaultTtlHours(SensitivityLevel.Sensitive).Should().Be(72);
        SensitiveResultGate.DefaultTtlHours(SensitivityLevel.HighlySensitive).Should().Be(24);
    }

    [Fact]
    public void A_request_requires_a_non_blank_justification()
    {
        SensitiveResultGate.IsRequestValid("continuity of care").Should().BeTrue();
        SensitiveResultGate.IsRequestValid("   ").Should().BeFalse();
    }

    // ---- grant lifecycle at the datastore -------------------------------------------------------
    [SkippableFact]
    public async Task A_grant_is_single_result_non_transferable_time_boxed_and_revocable()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        var lineX = Guid.NewGuid();
        var lineY = Guid.NewGuid();
        var granteeA = "userA-" + Guid.NewGuid().ToString("N")[..6];
        var granteeB = "userB-" + Guid.NewGuid().ToString("N")[..6];
        Guid requestId, grantId;
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using (var db = Ctx())
            {
                var req = new ReportAccessRequest
                {
                    RequestId = Guid.NewGuid(), OrderId = Guid.NewGuid(), OrderLineId = lineX, BeneficiaryId = beneficiary,
                    RequestedBy = granteeA, PurposeCode = PurposeCode.ContinuityOfCare, Justification = "care", RequestedTtlHours = 24,
                    Status = ReportAccessStatus.Approved, CreatedAt = now,
                };
                db.ReportAccessRequests.Add(req);
                await db.SaveChangesAsync();   // parent first (grant→request FK not modelled in EF)
                db.ReportAccessGrants.Add(new ReportAccessGrant
                {
                    GrantId = Guid.NewGuid(), RequestId = req.RequestId, GranteeUserId = granteeA, OrderLineId = lineX,
                    PurposeCode = PurposeCode.ContinuityOfCare, GrantedAt = now, ExpiresAt = now.AddHours(24),
                });
                await db.SaveChangesAsync();
                requestId = req.RequestId;
                grantId = (await db.ReportAccessGrants.SingleAsync(g => g.RequestId == req.RequestId)).GrantId;
            }

            await using (var db = Ctx())
            {
                var t = DateTimeOffset.UtcNow;
                Task<bool> Active(string user, Guid line) => db.ReportAccessGrants.AsNoTracking()
                    .AnyAsync(g => g.GranteeUserId == user && g.OrderLineId == line && g.RevokedAt == null && t < g.ExpiresAt);

                (await Active(granteeA, lineX)).Should().BeTrue("the grantee holds an active grant on the granted result");
                (await Active(granteeB, lineX)).Should().BeFalse("non-transferable — grantee B cannot use grantee A's grant");
                (await Active(granteeA, lineY)).Should().BeFalse("single-result — a grant for line X does not unlock line Y");
            }

            // Revoke → immediately inactive.
            await using (var db = Ctx())
            {
                var g = await db.ReportAccessGrants.SingleAsync(x => x.GrantId == grantId);
                g.RevokedAt = DateTimeOffset.UtcNow; g.RevokedBy = "author";
                await db.SaveChangesAsync();
            }
            await using (var db = Ctx())
            {
                var t = DateTimeOffset.UtcNow;
                (await db.ReportAccessGrants.AsNoTracking().AnyAsync(g => g.GrantId == grantId && g.RevokedAt == null && t < g.ExpiresAt))
                    .Should().BeFalse("a revoked grant is inactive");
            }
        }
        finally
        {
            await using var db = Ctx();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM orders.report_access_grant WHERE request_id IN (SELECT request_id FROM orders.report_access_request WHERE beneficiary_id = {0}); " +
                "DELETE FROM orders.report_access_request WHERE beneficiary_id = {0};", beneficiary);
        }
    }

    [SkippableFact]
    public async Task An_expired_grant_is_no_longer_active()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        var line = Guid.NewGuid();
        var grantee = "user-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var past = DateTimeOffset.UtcNow.AddHours(-1);
            await using (var db = Ctx())
            {
                var req = new ReportAccessRequest
                {
                    RequestId = Guid.NewGuid(), OrderId = Guid.NewGuid(), OrderLineId = line, BeneficiaryId = beneficiary,
                    RequestedBy = grantee, PurposeCode = PurposeCode.ClinicalReview, Justification = "review", Status = ReportAccessStatus.Approved,
                    CreatedAt = past.AddHours(-1),
                };
                db.ReportAccessRequests.Add(req);
                await db.SaveChangesAsync();   // parent first (grant→request FK not modelled in EF)
                db.ReportAccessGrants.Add(new ReportAccessGrant
                {
                    GrantId = Guid.NewGuid(), RequestId = req.RequestId, GranteeUserId = grantee, OrderLineId = line,
                    PurposeCode = PurposeCode.ClinicalReview, GrantedAt = past.AddHours(-2), ExpiresAt = past,   // already elapsed
                });
                await db.SaveChangesAsync();
            }
            await using var verify = Ctx();
            var now = DateTimeOffset.UtcNow;
            (await verify.ReportAccessGrants.AsNoTracking().AnyAsync(g => g.GranteeUserId == grantee && g.OrderLineId == line && g.RevokedAt == null && now < g.ExpiresAt))
                .Should().BeFalse("an expired grant no longer grants access");
        }
        finally
        {
            await using var db = Ctx();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM orders.report_access_grant WHERE request_id IN (SELECT request_id FROM orders.report_access_request WHERE beneficiary_id = {0}); " +
                "DELETE FROM orders.report_access_request WHERE beneficiary_id = {0};", beneficiary);
        }
    }
}
