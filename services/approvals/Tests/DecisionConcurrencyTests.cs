using FluentAssertions;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Tests;

/// <summary>Phase 7.2 decision atomicity at the datastore (env-gated <c>APPROVALS_TEST_DB</c>, real parallel PG,
/// NOT mocked): when N reviewers decide the SAME UnderReview case at once, the <c>xmin</c> optimistic-concurrency
/// guard means exactly ONE decision commits and the rest fail with a concurrency conflict (the endpoint maps that
/// to 409) — so a case is decided once, with a single ledger row. Serialized via the <c>approvals-db</c>
/// collection so the many-connection race doesn't collide with the other datastore tests.</summary>
[Collection("approvals-db")]
public class DecisionConcurrencyTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("APPROVALS_TEST_DB");
    private static DbContextOptions<ApprovalsDbContext> Options() =>
        new DbContextOptionsBuilder<ApprovalsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [Fact]
    public async Task Two_reviewers_deciding_the_same_case_exactly_one_wins()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            Guid id;
            await using (var seed = new ApprovalsDbContext(Options()))
            {
                var auth = new Authorization
                {
                    AuthorizationId = Guid.NewGuid(), AuthNo = await new AuthNoIssuer(seed).NextAsync(2026),
                    BeneficiaryId = beneficiary, Source = AuthSource.OrderLine, RequestingProviderId = Guid.NewGuid(),
                    ServiceCodes = "[\"70450\"]", Status = AuthStatus.UnderReview,
                    SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-10), SlaDueAt = DateTimeOffset.UtcNow.AddHours(1),
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                seed.Authorizations.Add(auth);
                await seed.SaveChangesAsync();
                id = auth.AuthorizationId;
            }

            const int racers = 8;
            using var barrier = new Barrier(racers);
            var wins = 0; var conflicts = 0;

            await Task.WhenAll(Enumerable.Range(0, racers).Select(i => Task.Run(async () =>
            {
                await using var ctx = new ApprovalsDbContext(Options());
                var auth = await ctx.Authorizations.FirstAsync(a => a.AuthorizationId == id);
                var decision = i % 2 == 0 ? AuthDecision.Approved : AuthDecision.Rejected;
                auth.Status = AuthorizationWorkflow.ResultOf(decision);
                auth.DecidedAt = DateTimeOffset.UtcNow;

                barrier.SignalAndWait();
                await using var tx = await ctx.Database.BeginTransactionAsync();
                try
                {
                    // Parent-first: the xmin guard rejects the losers here (same ordering as the endpoint) before
                    // the append-only child insert, so simultaneous deciders conflict cleanly instead of deadlocking.
                    await ctx.SaveChangesAsync();
                    ctx.Decisions.Add(new AuthorizationDecision
                    {
                        DecisionId = Guid.NewGuid(), AuthorizationId = id, Decision = decision,
                        ReviewerId = Guid.NewGuid(), DecidedAt = DateTimeOffset.UtcNow,
                        Rationale = decision == AuthDecision.Rejected ? "out of policy" : "within policy",
                    });
                    await ctx.SaveChangesAsync();
                    await tx.CommitAsync();
                    Interlocked.Increment(ref wins);
                }
                catch (DbUpdateConcurrencyException) { Interlocked.Increment(ref conflicts); }
            })));

            wins.Should().Be(1, "exactly one reviewer's decision may commit");
            conflicts.Should().Be(racers - 1);

            await using var verify = new ApprovalsDbContext(Options());
            var rows = await verify.Decisions.AsNoTracking().Where(d => d.AuthorizationId == id).CountAsync();
            rows.Should().Be(1, "only the winning decision leaves a ledger row");
            var final = await verify.Authorizations.AsNoTracking().SingleAsync(a => a.AuthorizationId == id);
            final.Status.Should().BeOneOf(AuthStatus.Approved, AuthStatus.Rejected);
        }
        finally { await Cleanup(beneficiary); }
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        if (Db is null) return;
        await using var ctx = new ApprovalsDbContext(Options());
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
