using FluentAssertions;
using Mersal.Approvals.Domain;
using Mersal.Approvals.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Approvals.Tests;

/// <summary>Phase 7.3 break-glass persistence + reporting at the datastore (env-gated <c>APPROVALS_TEST_DB</c>): a
/// break-glass decision must carry a justification (DB CHECK), such cases are flagged for the retrospective-review
/// queue, a manual authorization may be created + decided in one step with no requesting provider, and the TAT/SLA
/// aggregate (avg/p95/breach) computes over decided cases. Serialized via the <c>approvals-db</c> collection.</summary>
[Collection("approvals-db")]
public class BreakGlassTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("APPROVALS_TEST_DB");
    private static DbContextOptions<ApprovalsDbContext> Options() =>
        new DbContextOptionsBuilder<ApprovalsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [Fact]
    public async Task A_break_glass_decision_without_a_justification_is_rejected_by_db()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            await using var ctx = new ApprovalsDbContext(Options());
            var auth = Manual(beneficiary, await new AuthNoIssuer(ctx).NextAsync(2026));
            auth.Decisions.Add(new AuthorizationDecision
            {
                DecisionId = Guid.NewGuid(), AuthorizationId = auth.AuthorizationId, Decision = AuthDecision.EmergencyApproved,
                ReviewerId = Guid.NewGuid(), DecidedAt = DateTimeOffset.UtcNow, BreakGlass = true, Justification = null,
            });
            ctx.Authorizations.Add(auth);
            var act = async () => await ctx.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();   // CHECK (break_glass=false OR justification NOT NULL)
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task A_manual_break_glass_authorization_lands_in_the_retrospective_queue()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            Guid id;
            await using (var ctx = new ApprovalsDbContext(Options()))
            {
                var auth = Manual(beneficiary, await new AuthNoIssuer(ctx).NextAsync(2026));
                auth.Status = AuthStatus.Approved;
                auth.DecidedAt = DateTimeOffset.UtcNow;
                auth.TatSeconds = 0;
                auth.RetrospectiveReviewRequired = true;
                auth.Decisions.Add(new AuthorizationDecision
                {
                    DecisionId = Guid.NewGuid(), AuthorizationId = auth.AuthorizationId, Decision = AuthDecision.Approved,
                    ReviewerId = Guid.NewGuid(), DecidedAt = DateTimeOffset.UtcNow, BreakGlass = true,
                    Justification = "member present, provider offline", Rationale = "policy-covered service",
                });
                ctx.Authorizations.Add(auth);
                await ctx.SaveChangesAsync();
                id = auth.AuthorizationId;
            }

            await using (var read = new ApprovalsDbContext(Options()))
            {
                var queued = await read.Authorizations.AsNoTracking()
                    .Where(a => a.BeneficiaryId == beneficiary && a.RetrospectiveReviewRequired && !a.RetrospectiveReviewed)
                    .ToListAsync();
                queued.Should().ContainSingle(a => a.AuthorizationId == id);
                queued[0].Source.Should().Be(AuthSource.Manual);
                queued[0].RequestingProviderId.Should().BeNull();
            }

            // Once reviewed, it drops out of the queue.
            await using (var upd = new ApprovalsDbContext(Options()))
            {
                var a = await upd.Authorizations.SingleAsync(x => x.AuthorizationId == id);
                a.RetrospectiveReviewed = true;
                await upd.SaveChangesAsync();
            }
            await using (var read2 = new ApprovalsDbContext(Options()))
            {
                (await read2.Authorizations.CountAsync(a => a.BeneficiaryId == beneficiary
                    && a.RetrospectiveReviewRequired && !a.RetrospectiveReviewed)).Should().Be(0);
            }
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task Tat_summary_aggregates_avg_p95_and_breaches()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            await using (var ctx = new ApprovalsDbContext(Options()))
            {
                // Three decided Approved cases: TAT 100 / 200 / 900s; the 900 one breached SLA.
                foreach (var (tat, breached) in new[] { (100, false), (200, false), (900, true) })
                {
                    var auth = Manual(beneficiary, await new AuthNoIssuer(ctx).NextAsync(2026));
                    auth.Status = AuthStatus.Approved;
                    auth.DecidedAt = DateTimeOffset.UtcNow;
                    auth.TatSeconds = tat;
                    auth.SlaBreached = breached;
                    ctx.Authorizations.Add(auth);
                }
                await ctx.SaveChangesAsync();
            }

            await using var read = new ApprovalsDbContext(Options());
            var summary = await TatReporting.SummaryAsync(read);
            summary.Total.Should().BeGreaterThanOrEqualTo(3);
            summary.SlaBreaches.Should().BeGreaterThanOrEqualTo(1);
            summary.ByStatus.Should().Contain(b => b.Status == "Approved");
            var approved = summary.ByStatus.First(b => b.Status == "Approved");
            approved.AvgTatSeconds.Should().BeGreaterThan(0);
            approved.P95TatSeconds.Should().BeGreaterThanOrEqualTo(approved.AvgTatSeconds);
        }
        finally { await Cleanup(beneficiary); }
    }

    private static Authorization Manual(Guid beneficiary, string authNo) => new()
    {
        AuthorizationId = Guid.NewGuid(), AuthNo = authNo, BeneficiaryId = beneficiary,
        Source = AuthSource.Manual, RequestingProviderId = null, ServiceCodes = "[\"70450\"]",
        Status = AuthStatus.Submitted, SubmittedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

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
