using FluentAssertions;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Tests;

/// <summary>Phase 7.1 persistence at the datastore (env-gated <c>APPROVALS_TEST_DB</c>): an authorization
/// round-trips as Submitted, the auth-number issuer is monotonic, and — the key immutability guarantee — the
/// append-only <c>authorization_decision</c> ledger rejects UPDATE and DELETE via the DB trigger. Self-cleans by
/// beneficiary scope.</summary>
[Collection("approvals-db")]
public class ApprovalsIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("APPROVALS_TEST_DB");
    private static DbContextOptions<ApprovalsDbContext> Options() =>
        new DbContextOptionsBuilder<ApprovalsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [SkippableFact]
    public async Task Authorization_round_trips_as_submitted_with_a_monotonic_number()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            Guid id;
            await using (var ctx = new ApprovalsDbContext(Options()))
            {
                var no1 = await new AuthNoIssuer(ctx).NextAsync(2026);
                var no2 = await new AuthNoIssuer(ctx).NextAsync(2026);
                no1.Should().StartWith("AUTH-2026-");
                string.CompareOrdinal(no2, no1).Should().BeGreaterThan(0);

                var auth = new Authorization
                {
                    AuthorizationId = Guid.NewGuid(), AuthNo = no2, BeneficiaryId = beneficiary,
                    Source = AuthSource.OrderLine, SourceRef = Guid.NewGuid().ToString(),
                    RequestingProviderId = Guid.NewGuid(), ServiceCodes = "[\"70450\"]",
                    Priority = AuthPriority.Urgent, Status = AuthStatus.Submitted,
                    SubmittedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                ctx.Authorizations.Add(auth);
                await ctx.SaveChangesAsync();
                id = auth.AuthorizationId;
            }

            await using var verify = new ApprovalsDbContext(Options());
            var read = await verify.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == id);
            read.Status.Should().Be(AuthStatus.Submitted);
            read.Priority.Should().Be(AuthPriority.Urgent);
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_manual_authorization_may_omit_the_requesting_provider()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            await using var ctx = new ApprovalsDbContext(Options());
            var auth = new Authorization
            {
                AuthorizationId = Guid.NewGuid(), AuthNo = await new AuthNoIssuer(ctx).NextAsync(2026),
                BeneficiaryId = beneficiary, Source = AuthSource.Manual, RequestingProviderId = null,
                Status = AuthStatus.Submitted, SubmittedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            ctx.Authorizations.Add(auth);
            var act = async () => await ctx.SaveChangesAsync();
            await act.Should().NotThrowAsync();   // the requesting-provider CHECK exempts Manual
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task A_non_manual_authorization_without_a_provider_is_rejected_by_db()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            await using var ctx = new ApprovalsDbContext(Options());
            var auth = new Authorization
            {
                AuthorizationId = Guid.NewGuid(), AuthNo = await new AuthNoIssuer(ctx).NextAsync(2026),
                BeneficiaryId = beneficiary, Source = AuthSource.OrderLine, RequestingProviderId = null,
                Status = AuthStatus.Submitted, SubmittedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            ctx.Authorizations.Add(auth);
            var act = async () => await ctx.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();   // CHECK (source='Manual' OR provider NOT NULL)
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task The_decision_ledger_is_append_only()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            Guid decisionId;
            await using (var ctx = new ApprovalsDbContext(Options()))
            {
                var auth = new Authorization
                {
                    AuthorizationId = Guid.NewGuid(), AuthNo = await new AuthNoIssuer(ctx).NextAsync(2026),
                    BeneficiaryId = beneficiary, Source = AuthSource.OrderLine, RequestingProviderId = Guid.NewGuid(),
                    Status = AuthStatus.UnderReview, SubmittedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    Decisions =
                    [
                        new AuthorizationDecision
                        {
                            DecisionId = Guid.NewGuid(), Decision = AuthDecision.Approved,
                            ReviewerId = Guid.NewGuid(), DecidedAt = DateTimeOffset.UtcNow, Rationale = "within policy",
                        },
                    ],
                };
                ctx.Authorizations.Add(auth);
                await ctx.SaveChangesAsync();
                decisionId = auth.Decisions[0].DecisionId;
            }

            await using var ctx2 = new ApprovalsDbContext(Options());
            // A direct UPDATE is blocked by the append-only trigger.
            var update = async () => await ctx2.Database.ExecuteSqlRawAsync(
                "UPDATE approvals.authorization_decision SET rationale = 'tampered' WHERE decision_id = {0}", decisionId);
            await update.Should().ThrowAsync<Exception>();

            // As is a direct DELETE.
            var delete = async () => await ctx2.Database.ExecuteSqlRawAsync(
                "DELETE FROM approvals.authorization_decision WHERE decision_id = {0}", decisionId);
            await delete.Should().ThrowAsync<Exception>();
        }
        finally { await Cleanup(beneficiary); }
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var ctx = new ApprovalsDbContext(Options());
        // Decisions are FK children; the trigger blocks DELETE, so drop it transiently for teardown only.
        await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE approvals.authorization_decision DISABLE TRIGGER trg_auth_decision_no_mutate;");
        try
        {
            await ctx.Database.ExecuteSqlRawAsync(
                @"DELETE FROM approvals.authorization_decision d USING approvals.authorization a
                  WHERE d.authorization_id = a.authorization_id AND a.beneficiary_id = {0};", beneficiary);
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM approvals.authorization WHERE beneficiary_id = {0};", beneficiary);
        }
        finally
        {
            await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE approvals.authorization_decision ENABLE TRIGGER trg_auth_decision_no_mutate;");
        }
    }
}
