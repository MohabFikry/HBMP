using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>Line decisions at the datastore (env-gated <c>CLAIMS_TEST_DB</c>). Proves SoD (originator ≠ adjudicator,
/// no provider-affiliated self-decision, dual control needs two distinct approvers), mandatory reason/rationale,
/// append-only enforcement (UPDATE on claim_decision fails), concurrency (two officers → one 409), and roll-up to the
/// claim status. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class DecisionIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());
    private static DecisionService Svc(ClaimsDbContext db) => new(db, new BatchRollupService(db), TimeProvider.System);

    private static async Task<(Guid claimId, Guid lineId, Guid provider)> Seed(
        string tenant, string createdBy, decimal billed = 200m, decimal contract = 180m)
    {
        var provider = Guid.NewGuid();
        await using var db = Ctx();
        var claim = new Claim
        {
            ClaimId = Guid.NewGuid(), ClaimNo = await new ClaimNoIssuer(db).NextAsync(2026),
            Origin = ClaimOrigin.AutoDerived, BeneficiaryId = Guid.NewGuid(), ProviderId = provider,
            TenantId = tenant, ServiceDateFrom = new DateOnly(2026, 7, 1), CurrencyCode = "EGP",
            ClaimedAmount = billed, Status = ClaimStatus.UnderAdjudication, CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        claim.Lines.Add(new ClaimLine
        {
            ClaimLineId = Guid.NewGuid(), ClaimId = claim.ClaimId, CodeSystem = ClaimCodeSystem.CPT, Code = "80053",
            Quantity = 1, BilledAmount = billed, ContractPrice = contract, Status = ClaimLineStatus.Pending,
            FulfillmentRef = Guid.NewGuid(), FulfillmentType = FulfillmentType.OrderFulfillment,
        });
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return (claim.ClaimId, claim.Lines[0].ClaimLineId, provider);
    }

    private static DecisionRequest Approve() => new(ClaimDecisionKind.Approve, null, [], null, false, null);

    [SkippableFact]
    public async Task Originator_cannot_adjudicate_their_own_claim()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await Seed(tenant, createdBy: "alice");
            await using var db = Ctx();
            var r = await Svc(db).DecideAsync(tenant, "alice", null, claimId, lineId, Approve(), "k1", 1_000_000m, "c");
            r.Outcome.Should().Be(DecisionOutcome.SoDOriginator);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Provider_affiliated_user_cannot_decide_their_own_providers_claim()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, provider) = await Seed(tenant, createdBy: "system");
            await using var db = Ctx();
            var r = await Svc(db).DecideAsync(tenant, "bob", provider.ToString(), claimId, lineId, Approve(), "k1", 1_000_000m, "c");
            r.Outcome.Should().Be(DecisionOutcome.SoDProviderAffiliated);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Deny_without_rationale_is_rejected_and_records_nothing()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await Seed(tenant, "system");
            await using var db = Ctx();
            var req = new DecisionRequest(ClaimDecisionKind.Deny, null, [ReasonCodes.LimitExceeded], null, false, null);
            var r = await Svc(db).DecideAsync(tenant, "officer", null, claimId, lineId, req, "k1", 1_000_000m, "c");
            r.Outcome.Should().Be(DecisionOutcome.Validation);
            r.ValidationError.Should().Be("rationale-required");
            (await Ctx().ClaimDecisions.CountAsync(d => d.ClaimId == claimId)).Should().Be(0);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Approve_records_an_appendonly_decision_and_rolls_up_to_the_claim()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await Seed(tenant, "system");
            Guid decisionId;
            await using (var db = Ctx())
            {
                var r = await Svc(db).DecideAsync(tenant, "officer", null, claimId, lineId, Approve(), "k1", 1_000_000m, "c");
                r.Outcome.Should().Be(DecisionOutcome.Recorded);
                r.ClaimTerminal.Should().BeTrue();
                decisionId = r.Decision!.DecisionId;
            }
            await using var verify = Ctx();
            (await verify.ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == lineId)).Status.Should().Be(ClaimLineStatus.Approved);
            (await verify.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == claimId)).Status.Should().Be(ClaimStatus.Approved);

            // append-only: a direct UPDATE on the decision ledger is rejected by the trigger.
            var act = async () => await verify.Database.ExecuteSqlRawAsync(
                "UPDATE claims.claim_decision SET rationale = 'tamper' WHERE decision_id = {0}", decisionId);
            await act.Should().ThrowAsync<Exception>();
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Dual_control_needs_a_second_distinct_approver()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await Seed(tenant, "system", billed: 200m);
            Guid pendingId;
            // value 200 > threshold 100 → pending.
            await using (var db = Ctx())
            {
                var r = await Svc(db).DecideAsync(tenant, "officer-1", null, claimId, lineId, Approve(), "k1", 100m, "c");
                r.Outcome.Should().Be(DecisionOutcome.PendingSecondApproval);
                pendingId = r.Decision!.DecisionId;
            }
            // the SAME approver cannot confirm.
            await using (var db = Ctx())
            {
                var same = new DecisionRequest(ClaimDecisionKind.Approve, null, [], null, false, pendingId);
                (await Svc(db).DecideAsync(tenant, "officer-1", null, claimId, lineId, same, "k2", 100m, "c")).Outcome
                    .Should().Be(DecisionOutcome.SoDSameDecider);
            }
            // a DIFFERENT approver confirms → applied.
            await using (var db = Ctx())
            {
                var conf = new DecisionRequest(ClaimDecisionKind.Approve, null, [], null, false, pendingId);
                (await Svc(db).DecideAsync(tenant, "officer-2", null, claimId, lineId, conf, "k3", 100m, "c")).Outcome
                    .Should().Be(DecisionOutcome.Confirmed);
            }
            (await Ctx().ClaimLines.AsNoTracking().SingleAsync(l => l.ClaimLineId == lineId)).Status
                .Should().Be(ClaimLineStatus.Approved);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Two_officers_deciding_the_same_line_yield_one_winner()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await Seed(tenant, "system");
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
            {
                await using var db = Ctx();
                return (await Svc(db).DecideAsync(tenant, $"officer-{i}", null, claimId, lineId, Approve(), $"key-{i}", 1_000_000m, "c")).Outcome;
            }));
            outcomes.Count(o => o == DecisionOutcome.Recorded).Should().Be(1);
            outcomes.Count(o => o == DecisionOutcome.Conflict).Should().Be(1);
            (await Ctx().ClaimDecisions.CountAsync(d => d.ClaimLineId == lineId)).Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task Cleanup(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        // claim_decision is append-only (trigger blocks DELETE); disable user triggers for this cleanup session only.
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM claims.claim_decision WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", tenant);
    }
}
