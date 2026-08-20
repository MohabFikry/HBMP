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

    /// <summary>
    /// The same double-decision as the concurrency test above, but SEQUENTIAL — and it is the sequential
    /// case that matters, because it is the one that actually happens.
    ///
    /// <para>Two officers deciding one line were only ever stopped by an optimistic-concurrency collision on
    /// <c>claim_line.xmin</c>, which fires when their transactions OVERLAP. Nothing refused a second decision
    /// on a line that already had a terminal one. So the guard held whenever the two requests raced and
    /// vanished whenever they merely followed one another — which is the ordinary case: two officers working
    /// the same worklist a second apart, or one retrying after a timeout on a request that had in fact
    /// succeeded.</para>
    ///
    /// <para>The consequence is money. The second decision appends another <c>claim_decision</c> row and
    /// overwrites <c>claim_line.allowed_amount</c>, so the settled figure is whichever officer happened to
    /// go last, with the first decision still in the append-only ledger looking authoritative.</para>
    ///
    /// <para>Found because the concurrency test failed on CI's faster runner — where the two tasks completed
    /// far enough apart to serialise — while passing locally. A test that only fails when the timing is
    /// unlucky is a test that describes the bug it was written to prevent.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_second_officer_cannot_decide_a_line_that_is_already_decided()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await Seed(tenant, "system");

            await using (var db = Ctx())
            {
                var first = await Svc(db).DecideAsync(tenant, "officer-0", null, claimId, lineId,
                    Approve(), "seq-key-0", 1_000_000m, "c");
                first.Outcome.Should().Be(DecisionOutcome.Recorded);
            }

            // A completely separate context, after the first has committed — no overlap, so no xmin
            // collision to lean on. Different actor and different key, so neither the SoD-same-decider
            // check nor the idempotency replay applies. Only an explicit "already decided" rule can refuse.
            await using (var db = Ctx())
            {
                var second = await Svc(db).DecideAsync(tenant, "officer-1", null, claimId, lineId,
                    Approve(), "seq-key-1", 1_000_000m, "c");
                second.Outcome.Should().Be(DecisionOutcome.Conflict,
                    "a line with a terminal decision is closed to further decisions; re-opening one is what " +
                    "the appeal flow is for (AppealService.RaiseAsync), and it goes through a different door");
            }

            (await Ctx().ClaimDecisions.CountAsync(d => d.ClaimLineId == lineId)).Should().Be(1,
                "a second decision row on a decided line makes the settled amount depend on who went last");
        }
        finally { await Cleanup(tenant); }
    }

    /// <summary>
    /// A DENY retried under a key already used to APPROVE is refused, not answered with the approval.
    /// </summary>
    /// <remarks>
    /// The replay compared the key alone until the 2026-08-09 audit. So an officer who approved, then
    /// corrected themselves and denied under the same key, was told 200 OK — and read the APPROVAL back. The
    /// line stays payable, the denial they believe they recorded does not exist, and there is no error
    /// anywhere to investigate: from the platform's side nothing went wrong. Money moves on this.
    /// </remarks>
    [SkippableFact]
    public async Task A_deny_retried_under_an_approves_key_is_refused_rather_than_answered_approved()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await Seed(tenant, "alice");
            await using var db = Ctx();

            (await Svc(db).DecideAsync(tenant, "officer", null, claimId, lineId, Approve(), "k1", 1_000_000m, "c"))
                .Outcome.Should().Be(DecisionOutcome.Recorded);

            var deny = new DecisionRequest(
                ClaimDecisionKind.Deny, null, ["NOT_COVERED"], "outside the benefit", false, null);
            var reused = await Svc(db).DecideAsync(tenant, "officer", null, claimId, lineId, deny, "k1", 1_000_000m, "c");

            reused.Outcome.Should().Be(DecisionOutcome.IdempotencyKeyReuse);

            await using var verify = Ctx();
            var decisions = await verify.ClaimDecisions.AsNoTracking().Where(d => d.ClaimId == claimId).ToListAsync();
            decisions.Should().ContainSingle("the refusal changes nothing — the officer retries with their own key");
            decisions[0].Decision.Should().Be(ClaimDecisionKind.Approve);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task The_SAME_decision_retried_under_the_same_key_still_replays()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            var (claimId, lineId, _) = await Seed(tenant, "alice");
            await using var db = Ctx();

            (await Svc(db).DecideAsync(tenant, "officer", null, claimId, lineId, Approve(), "k1", 1_000_000m, "c"))
                .Outcome.Should().Be(DecisionOutcome.Recorded);

            // The point of the header, and it has to survive the new check: a retry after a dropped response
            // returns the decision already made, not a second one and not a 422.
            (await Svc(db).DecideAsync(tenant, "officer", null, claimId, lineId, Approve(), "k1", 1_000_000m, "c"))
                .Outcome.Should().Be(DecisionOutcome.Replayed);

            await using var verify = Ctx();
            (await verify.ClaimDecisions.AsNoTracking().CountAsync(d => d.ClaimId == claimId)).Should().Be(1);
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
