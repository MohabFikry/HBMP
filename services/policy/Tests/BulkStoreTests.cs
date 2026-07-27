using System.Globalization;
using System.Text;
using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.5b against real Postgres (env-gated <c>POLICY_TEST_DB</c>, migration 0014 applied).
///
/// <para>These are the acceptance criteria that only mean anything against a database: committing twice must
/// not apply twice (the unique index on <c>enrollment.idempotency_key</c> is what makes that true), a bad row
/// must fail alone inside its own transaction, and an as-of extract must reconstruct a member's plan from
/// <c>enrollment_event</c> rather than read it off the current row.</para>
/// </summary>
[Collection("policy-db")]
public class BulkStoreTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    // ---- Commit: idempotency + partial failure ------------------------------------------------------------

    [SkippableFact]
    public async Task Committing_the_same_job_twice_applies_every_row_exactly_once()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);
            var job = await harness.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, f.Beneficiaries));
            await harness.Engine.ValidateAsync(job.JobId, harness.Scope, null);

            var first = await harness.Engine.CommitAsync(job.JobId, harness.Scope, null);
            first.Job.AppliedRows.Should().Be(3);

            // Two layers protect against a double apply, and both are worth asserting because they fail in
            // different circumstances.
            //
            // 1. ROW STATE. An Applied row is not re-processed. This is what a plain re-commit hits.
            await db.BulkJobs.Where(j => j.JobId == job.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, BulkJobStatus.Validated));
            db.ChangeTracker.Clear();
            var second = await harness.Engine.CommitAsync(job.JobId, harness.Scope, null);
            second.Job.AppliedRows.Should().Be(0);

            // 2. THE IDEMPOTENCY KEY. This is the layer that survives a crash between "the membership was
            // written" and "the row was marked Applied" — the state a resumed job actually finds itself in, and
            // the one row state alone cannot protect. Forcing the rows back to Valid reproduces it exactly.
            await db.BulkJobRows.Where(r => r.JobId == job.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, BulkRowStatus.Valid));
            await db.BulkJobs.Where(j => j.JobId == job.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, BulkJobStatus.Validated));
            db.ChangeTracker.Clear();

            var resumed = await harness.Engine.CommitAsync(job.JobId, harness.Scope, null);
            resumed.Job.AppliedRows.Should().Be(0);
            resumed.Job.SkippedRows.Should().Be(3, "the key is (job, row) and is stable across every retry");

            var created = await db.Enrollments.CountAsync(
                e => f.Beneficiaries.Contains(e.BeneficiaryId) && !e.IsDeleted);
            created.Should().Be(3, "a resumed job must not create a second membership for anybody");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_row_that_breaches_a_rule_fails_alone_and_the_rest_still_apply()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);
            // Row 2 names a policy that does not exist. Aborting the file on it would leave the job
            // half-applied with no record of where it stopped — not atomic, and not accounted for either.
            var csv = new StringBuilder("beneficiary_id,policy_no,relationship,effective_from\n");
            csv.Append(CultureInfo.InvariantCulture, $"{f.Beneficiaries[0]},{f.PolicyNo},Principal,2026-02-01\n");
            csv.Append(CultureInfo.InvariantCulture, $"{f.Beneficiaries[1]},NO-SUCH-POLICY,Principal,2026-02-01\n");
            csv.Append(CultureInfo.InvariantCulture, $"{f.Beneficiaries[2]},{f.PolicyNo},Principal,2026-02-01\n");

            var job = await harness.UploadAsync(BulkJobType.MemberEnrolment, Encoding.UTF8.GetBytes(csv.ToString()));
            var validation = await harness.Engine.ValidateAsync(job.JobId, harness.Scope, null);

            validation.Job.ValidRows.Should().Be(2);
            validation.Job.InvalidRows.Should().Be(1);
            validation.Errors.Should().ContainSingle().Which.RowNumber.Should().Be(2);
            validation.Errors[0].DetailAr.Should().NotBeNullOrWhiteSpace();
            // NOTHING is applied until commit.
            (await db.Enrollments.CountAsync(e => f.Beneficiaries.Contains(e.BeneficiaryId))).Should().Be(0);

            var commit = await harness.Engine.CommitAsync(job.JobId, harness.Scope, null);
            commit.Job.AppliedRows.Should().Be(2);
            commit.Job.Status.Should().Be(BulkJobStatus.Completed, "completed-with-errors is the normal outcome");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_dry_run_reports_what_would_change_without_changing_it()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);
            var job = await harness.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, [f.Beneficiaries[0]]));

            var validation = await harness.Engine.ValidateAsync(job.JobId, harness.Scope, null);

            validation.Preview.Should().ContainSingle();
            validation.Preview[0].SummaryAr.Should().NotBeNullOrWhiteSpace();
            validation.Preview[0].Changes.Should().ContainKey("policyNo");
            (await db.Enrollments.CountAsync(e => e.BeneficiaryId == f.Beneficiaries[0])).Should().Be(0);
        }
        finally { await Cleanup(f); }
    }

    // ---- Scope --------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_bulk_file_cannot_reach_outside_the_submitters_payer_scope()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            // Restricted to a payer that is not this policy's. The file names a policy that plainly exists —
            // and one line among ten thousand is exactly the shape reaching outside one's own book of business
            // would take, which is why the check is per ROW rather than once at the edge.
            var harness = new Harness(db, f, PermittedPayers.RestrictedTo([Guid.NewGuid()]));
            var job = await harness.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, [f.Beneficiaries[0]]));

            var validation = await harness.Engine.ValidateAsync(job.JobId, harness.Scope, null);

            validation.Job.ValidRows.Should().Be(0);
            validation.Errors.Should().ContainSingle().Which.Code.Should().Be("OUT_OF_SCOPE");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_back_dated_termination_in_bulk_needs_the_same_supervisory_scope_as_one_in_the_form()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var enrolled = new Harness(db, f);
            var job = await enrolled.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, [f.Beneficiaries[0]]));
            await enrolled.Engine.ValidateAsync(job.JobId, enrolled.Scope, null);
            await enrolled.Engine.CommitAsync(job.JobId, enrolled.Scope, null);
            var memberNo = await db.Enrollments.Where(e => e.BeneficiaryId == f.Beneficiaries[0])
                .Select(e => e.MemberNo).FirstAsync();

            // A thousand back-dated terminations is the case that check matters most for.
            var unsupervised = new Harness(db, f, maySupervise: false);
            var csv = Encoding.UTF8.GetBytes(
                "member_no,effective_date,reason\n" + $"{memberNo},2026-02-05,programme ended\n");
            var terminationJob = await unsupervised.UploadAsync(BulkJobType.MemberTermination, csv);
            await unsupervised.Engine.ValidateAsync(terminationJob.JobId, unsupervised.Scope, null);
            var commit = await unsupervised.Engine.CommitAsync(terminationJob.JobId, unsupervised.Scope, null);

            commit.Job.FailedRows.Should().Be(1);
            commit.Errors.Should().ContainSingle().Which.Code.Should().Be("SUPERVISION_REQUIRED");
        }
        finally { await Cleanup(f); }
    }

    // ---- Rollback -----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Rollback_reverses_the_rows_it_can_and_refuses_the_ones_where_benefit_was_consumed()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);
            var job = await harness.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, f.Beneficiaries));
            await harness.Engine.ValidateAsync(job.JobId, harness.Scope, null);
            await harness.Engine.CommitAsync(job.JobId, harness.Scope, null);
            db.ChangeTracker.Clear();

            // One member has since used their benefit. Cancelling that membership would leave the consumption
            // attached to an entitlement the record no longer admits to.
            var consumer = await db.Enrollments.FirstAsync(e => e.BeneficiaryId == f.Beneficiaries[0]);
            await db.CoverageLimits
                .Where(l => db.Coverages.Where(c => c.EnrollmentId == consumer.EnrollmentId)
                    .Select(c => c.CoverageId).Contains(l.CoverageId))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ConsumedValue, 40m));
            db.ChangeTracker.Clear();

            var rollback = await harness.Engine.RollBackAsync(job.JobId, harness.Scope, "uploaded in error", null);

            rollback.Reversed.Should().Be(2);
            rollback.RefusedRows.Should().ContainSingle().Which.Code.Should().Be("CONSUMPTION_EXISTS");
            rollback.RefusedRows[0].DetailAr.Should().NotBeNullOrWhiteSpace();
            // Partial. Refusing all three because of the one would leave the operator with no path at all —
            // and a job reported as RolledBack when it was not is the most dangerous state here.
            rollback.Job.Status.Should().Be(BulkJobStatus.Completed);

            db.ChangeTracker.Clear();
            var cancelled = await db.Enrollments.CountAsync(
                e => f.Beneficiaries.Contains(e.BeneficiaryId) && e.Status == EnrollmentStatus.Cancelled);
            cancelled.Should().Be(2);
            (await db.Enrollments.FirstAsync(e => e.EnrollmentId == consumer.EnrollmentId))
                .Status.Should().Be(EnrollmentStatus.Active, "the consumed membership is untouched");
        }
        finally { await Cleanup(f); }
    }

    // ---- The infected file --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_infected_file_fails_at_scanning_and_nothing_is_parsed()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f, documents: new InfectedDocumentStore());

            var job = await harness.Engine.UploadAsync(
                BulkJobType.MemberEnrolment, "members.csv", "text/csv", EnrolmentCsv(f, f.Beneficiaries),
                harness.Scope.Actor, "tester", null);

            job.Status.Should().Be(BulkJobStatus.Failed);
            job.FailureCode.Should().Be("FILE_INFECTED");
            job.TotalRows.Should().Be(0);
            (await db.BulkJobRows.CountAsync(r => r.JobId == job.JobId)).Should().Be(0);
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task A_file_that_could_not_be_scanned_is_not_parsed_either()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            // "The scanner was unavailable" and "the file is clean" are not the same answer.
            var harness = new Harness(db, f, documents: new UnavailableDocumentStore());

            var job = await harness.Engine.UploadAsync(
                BulkJobType.MemberEnrolment, "members.csv", "text/csv", EnrolmentCsv(f, f.Beneficiaries),
                harness.Scope.Actor, "tester", null);

            job.Status.Should().Be(BulkJobStatus.Failed);
            job.FailureCode.Should().Be("SCAN_UNAVAILABLE");
            (await db.BulkJobRows.CountAsync(r => r.JobId == job.JobId)).Should().Be(0);
        }
        finally { await Cleanup(f); }
    }

    // ---- Traceability -------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Every_applied_row_is_audited_with_its_job_and_row_number()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);
            var job = await harness.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, f.Beneficiaries));
            await harness.Engine.ValidateAsync(job.JobId, harness.Scope, null);
            await harness.Engine.CommitAsync(job.JobId, harness.Scope, null);

            // The thread from a member record back to the upload that created it. "A bulk job ran" cannot
            // answer "where did this membership come from".
            var rowEvents = harness.Audit.Drafts
                .Where(d => d.EntityType == "bulk_job_row" && d.Action == AuditAction.Create).ToList();
            rowEvents.Should().HaveCount(3);
            rowEvents.Should().OnlyContain(d => d.EntityId!.StartsWith(job.JobId.ToString(), StringComparison.Ordinal));

            var reconciliation = await harness.Engine.ReconcileAsync(job.JobId);
            reconciliation.Applied.Should().Be(3);
            reconciliation.Balances.Should().BeTrue();
        }
        finally { await Cleanup(f); }
    }

    // ---- As-of extraction ---------------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_as_of_extract_shows_a_member_terminated_after_that_date_and_the_plan_they_were_on()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);

            // Two members enrolled in January. One is terminated on 15 March; the other moves to the second
            // plan on 10 March. As of 1 March, neither of those has happened yet.
            var job = await harness.UploadAsync(BulkJobType.MemberEnrolment,
                EnrolmentCsv(f, [f.Beneficiaries[0], f.Beneficiaries[1]], from: new DateOnly(2026, 1, 5)));
            await harness.Engine.ValidateAsync(job.JobId, harness.Scope, null);
            await harness.Engine.CommitAsync(job.JobId, harness.Scope, null);
            db.ChangeTracker.Clear();

            var terminated = await db.Enrollments.FirstAsync(e => e.BeneficiaryId == f.Beneficiaries[0]);
            var moved = await db.Enrollments.FirstAsync(e => e.BeneficiaryId == f.Beneficiaries[1]);

            await harness.Membership.TerminateAsync(
                terminated.EnrollmentId, new DateOnly(2026, 3, 15), "programme ended",
                maySupervise: true, harness.Scope.Actor);
            db.ChangeTracker.Clear();
            await harness.Membership.ChangePlanAsync(
                moved.EnrollmentId, f.SecondPolicyPlanId, new DateOnly(2026, 3, 10), "moved", harness.Scope.Actor);
            db.ChangeTracker.Clear();

            var result = await harness.Extracts.RunAsync(
                new ExtractRequest(ExtractEntity.Members,
                    new ExtractFilter(PolicyId: f.PolicyId, AsOf: new DateOnly(2026, 3, 1)),
                    ["member_no", "status", "plan_label"]),
                new ExtractCapabilities(true, true, true, true), PermittedPayers.Unrestricted,
                harness.Scope.Actor, "tester", null);

            var csv = Encoding.UTF8.GetString(result.Inline!);
            csv.Should().Contain(terminated.MemberNo, "they were covered on 1 March; the termination applies from 15 March");
            // The member who moved plan on 10 March shows their 1-MARCH plan, not today's.
            var movedLine = csv.Split('\n').First(l => l.Contains(moved.MemberNo, StringComparison.Ordinal));
            movedLine.Should().Contain("Standard").And.NotContain("Enhanced");
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task An_extract_withholds_the_columns_a_role_may_not_see_and_names_them()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);

            var result = await harness.Extracts.RunAsync(
                new ExtractRequest(ExtractEntity.Members, new ExtractFilter(PolicyId: f.PolicyId),
                    ["member_no", "total_consumed", "diagnosis"]),
                new ExtractCapabilities(Amounts: false, Contract: false, Case: false, Identity: false),
                PermittedPayers.Unrestricted, harness.Scope.Actor, "finance", null);

            result.Columns.Should().Equal("member_no");
            result.Withheld.Select(w => w.ReasonCode).Should()
                .BeEquivalentTo(["ROLE_NOT_PERMITTED", "CLINICAL_NEVER_EXTRACTED"]);

            var run = await db.ExtractRuns.AsNoTracking().FirstAsync(r => r.RunId == result.Run!.RunId);
            run.WithheldSnapshot.Should().NotBeNull("the run records what was withheld, not only what was sent");
            run.FilterSnapshot.Should().Contain(f.PolicyId.ToString());
        }
        finally { await Cleanup(f); }
    }

    [SkippableFact]
    public async Task An_extract_is_narrowed_by_payer_scope_like_every_other_read()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);
            var job = await harness.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, f.Beneficiaries));
            await harness.Engine.ValidateAsync(job.JobId, harness.Scope, null);
            await harness.Engine.CommitAsync(job.JobId, harness.Scope, null);
            db.ChangeTracker.Clear();

            var mine = await harness.Extracts.RunAsync(
                new ExtractRequest(ExtractEntity.Members, new ExtractFilter(), ["member_no"]),
                new ExtractCapabilities(true, true, true, true),
                PermittedPayers.RestrictedTo([f.PayerId]), harness.Scope.Actor, "tester", null);
            var others = await harness.Extracts.RunAsync(
                new ExtractRequest(ExtractEntity.Members, new ExtractFilter(), ["member_no"]),
                new ExtractCapabilities(true, true, true, true),
                PermittedPayers.RestrictedTo([Guid.NewGuid()]), harness.Scope.Actor, "tester", null);

            mine.RowCount.Should().BeGreaterThanOrEqualTo(3);
            others.RowCount.Should().Be(0, "a restricted caller extracts nothing of another payer's book");
        }
        finally { await Cleanup(f); }
    }

    // ---- Harness ------------------------------------------------------------------------------------------

    private sealed class Harness
    {
        public Harness(
            PolicyDbContext db, Fixture f, PermittedPayers? payers = null, bool maySupervise = true,
            IOperationalDocumentStore? documents = null)
        {
            Audit = new RecordingAudit();
            var clock = TimeProvider.System;
            var calendar = new BusinessCalendar(clock);
            Membership = new MembershipCommands(
                db, new ActiveBeneficiaries(), new SequentialMemberNos(), Audit, new NullOutbox(),
                calendar, Options.Create(new MembershipOptions()), clock);

            IBulkRowApplier[] appliers =
            [
                new MemberEnrolmentApplier(db, Membership, new ActiveBeneficiaries()),
                new MemberTerminationApplier(db, Membership, calendar),
                new PlanChangeApplier(db, Membership, calendar),
                new GroupAssignmentApplier(db, Membership, calendar),
            ];

            Documents = documents ?? new NullDocumentStore();
            Engine = new BulkJobEngine(db, new BulkFileParser(), appliers, Documents, Audit, new NullOutbox(),
                clock, NullLogger<BulkJobEngine>.Instance);
            Extracts = new ExtractEngine(db, Documents, Audit, clock);
            Scope = new BulkScope
            {
                Actor = new ActorRef(Guid.NewGuid(), "bulk-tester"),
                Payers = payers ?? PermittedPayers.Unrestricted,
                MaySupervise = maySupervise,
            };
            _fixture = f;
        }

        private readonly Fixture _fixture;

        public RecordingAudit Audit { get; }
        public IOperationalDocumentStore Documents { get; }
        public MembershipCommands Membership { get; }
        public BulkJobEngine Engine { get; }
        public ExtractEngine Extracts { get; }
        public BulkScope Scope { get; }

        public Task<BulkJob> UploadAsync(BulkJobType type, byte[] csv) =>
            Engine.UploadAsync(type, $"{_fixture.Prefix}.csv", "text/csv", csv, Scope.Actor, "tester", null);
    }

    private sealed class RecordingAudit : IAuditClient
    {
        public List<AuditEventDraft> Drafts { get; } = [];

        public ValueTask EmitAsync(AuditEventDraft draft, CancellationToken ct = default)
        {
            Drafts.Add(draft);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullOutbox : IOutbox
    {
        public ValueTask EnqueueAsync<T>(string eventType, string destination, T payload, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask EnqueueRawAsync(OutboxMessage message, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class ActiveBeneficiaries : IBeneficiaryStatusProbe
    {
        public Task<string?> GetStatusAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
            => Task.FromResult<string?>("Active");
    }

    private sealed class SequentialMemberNos : IMemberNoIssuer
    {
        private int _next;

        public Task<string> NextAsync(DateOnly effectiveFrom, CancellationToken ct = default)
        {
            var next = Interlocked.Increment(ref _next);
            return Task.FromResult($"BULK-{Guid.NewGuid():N}"[..12]
                + "-" + next.ToString("D3", CultureInfo.InvariantCulture));
        }
    }

    private sealed class NullDocumentStore : IOperationalDocumentStore
    {
        public Task<Guid?> StoreAsync(
            string kind, Guid ownerRef, string fileName, string contentType, byte[] bytes,
            string? bearerToken, CancellationToken ct = default) => Task.FromResult<Guid?>(Guid.NewGuid());
    }

    private sealed class InfectedDocumentStore : IOperationalDocumentStore
    {
        public Task<Guid?> StoreAsync(
            string kind, Guid ownerRef, string fileName, string contentType, byte[] bytes,
            string? bearerToken, CancellationToken ct = default) => throw new BulkFileInfectedException("Eicar-Test-Signature");
    }

    private sealed class UnavailableDocumentStore : IOperationalDocumentStore
    {
        public Task<Guid?> StoreAsync(
            string kind, Guid ownerRef, string fileName, string contentType, byte[] bytes,
            string? bearerToken, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
    }

    // ---- Fixture ------------------------------------------------------------------------------------------

    private sealed record Fixture(
        string Prefix, Guid PayerId, Guid PolicyId, string PolicyNo,
        IReadOnlyList<Guid> PlanIds, IReadOnlyList<Guid> PlanVersionIds,
        Guid PolicyPlanId, Guid SecondPolicyPlanId, Guid CategoryId, IReadOnlyList<Guid> Beneficiaries);

    private static byte[] EnrolmentCsv(Fixture f, IReadOnlyList<Guid> beneficiaries, DateOnly? from = null)
    {
        var effective = from ?? new DateOnly(2026, 2, 1);
        var csv = new StringBuilder("beneficiary_id,policy_no,plan_label,relationship,effective_from\n");
        foreach (var id in beneficiaries)
            csv.Append(CultureInfo.InvariantCulture, $"{id},{f.PolicyNo},Standard,Principal,{effective:yyyy-MM-dd}\n");
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static async Task<Fixture> Seed()
    {
        await using var db = Ctx();
        var prefix = $"B{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        var category = await db.BenefitCategories.FirstOrDefaultAsync(c => c.Code == "LAB");
        if (category is null)
        {
            category = new BenefitCategory
            {
                BenefitCategoryId = Guid.NewGuid(), TenantId = Tenant, Code = "LAB", Name = "Laboratory",
            };
            db.BenefitCategories.Add(category);
            await db.SaveChangesAsync();
        }

        var payer = new Payer
        {
            PayerId = Guid.NewGuid(), TenantId = Tenant, PayerCode = prefix[..9],
            NameEn = "Bulk Payer", NameAr = "Bulk Payer", PayerType = PayerType.Donor,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        // TWO plans, because the two policy_plans below must point at DIFFERENT plan versions: the 0008
        // exclusion constraint forbids two Active policy_plan rows on the same policy and version with
        // overlapping windows, which is the invariant that stops one policy offering the same package twice.
        var plan = NewPlan(prefix, "A");
        var enhancedPlan = NewPlan(prefix, "B");
        db.Payers.Add(payer);
        db.Plans.AddRange(plan, enhancedPlan);
        await db.SaveChangesAsync();

        // Seeded as Draft and promoted below: benefit rules cannot be INSERTED under a non-Draft version
        // (0005's immutability trigger).
        var version = NewVersion(plan.PlanId, category.BenefitCategoryId, 100m);
        var enhancedVersion = NewVersion(enhancedPlan.PlanId, category.BenefitCategoryId, 250m);
        var policy = new Domain.Policy
        {
            PolicyId = Guid.NewGuid(), TenantId = Tenant, PolicyNo = $"{prefix}-POL",
            PayerId = payer.PayerId, EffectiveFrom = new DateOnly(2026, 1, 1), Status = PolicyStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PlanVersions.AddRange(version, enhancedVersion);
        db.Policies.Add(policy);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE policy.plan_version SET status = 'Active', activated_at = now() WHERE plan_version_id = ANY({0})",
                new[] { version.PlanVersionId, enhancedVersion.PlanVersionId });
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }

        // Two plans on the same policy so a plan CHANGE has somewhere to go.
        var standard = new PolicyPlan
        {
            PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId,
            PlanVersionId = version.PlanVersionId, PlanLabel = "Standard",
            EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var enhanced = new PolicyPlan
        {
            PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId,
            PlanVersionId = enhancedVersion.PlanVersionId, PlanLabel = "Enhanced",
            EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PolicyPlans.AddRange(standard, enhanced);
        await db.SaveChangesAsync();

        return new Fixture(prefix, payer.PayerId, policy.PolicyId, policy.PolicyNo,
            [plan.PlanId, enhancedPlan.PlanId], [version.PlanVersionId, enhancedVersion.PlanVersionId],
            standard.PolicyPlanId, enhanced.PolicyPlanId, category.BenefitCategoryId,
            [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);
    }

    private static Plan NewPlan(string prefix, string suffix) => new()
    {
        PlanId = Guid.NewGuid(), TenantId = Tenant, PlanCode = $"{prefix}{suffix}",
        NameEn = "Bulk", NameAr = "Bulk", Category = "Primary",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static PlanVersion NewVersion(Guid planId, Guid categoryId, decimal limit) => new()
    {
        PlanVersionId = Guid.NewGuid(), TenantId = Tenant, PlanId = planId, VersionNo = 1,
        EffectiveFrom = new DateOnly(2026, 1, 1), Status = PlanVersionStatus.Draft, ActivatedAt = null,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        Rules =
        [
            new BenefitRule
            {
                RuleId = Guid.NewGuid(), TenantId = Tenant, BenefitCategoryId = categoryId,
                IsCovered = true, LimitType = LimitType.Annual, LimitValue = limit,
                ResetPeriod = ResetPeriod.Yearly, WaitingPeriodDays = 0, Exclusions = "[]",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            },
        ],
    };

    private static async Task Cleanup(Fixture f)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version DISABLE TRIGGER trg_plan_version_immutable");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.benefit_rule DISABLE TRIGGER trg_benefit_rule_immutable");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.bulk_job_row DISABLE TRIGGER trg_bulk_job_row_immutable");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.extract_run DISABLE TRIGGER trg_extract_run_no_delete");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.enrollment_event DISABLE TRIGGER trg_enrollment_event_append_only");
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.coverage_limit WHERE coverage_id IN (SELECT coverage_id FROM policy.coverage WHERE policy_id = {0})",
                f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.coverage WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.enrollment_event WHERE enrollment_id IN (SELECT enrollment_id FROM policy.enrollment WHERE policy_id = {0})",
                f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.enrollment WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.bulk_job_row WHERE job_id IN (SELECT job_id FROM policy.bulk_job WHERE file_name LIKE {0})",
                f.Prefix + "%");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.bulk_job WHERE file_name LIKE {0}", f.Prefix + "%");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.extract_run WHERE filter_snapshot::text LIKE {0}",
                "%" + f.PolicyId + "%");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy_plan WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy WHERE policy_id = {0}", f.PolicyId);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.benefit_rule WHERE plan_version_id = ANY({0})", f.PlanVersionIds.ToArray());
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM policy.plan_version WHERE plan_version_id = ANY({0})", f.PlanVersionIds.ToArray());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.plan WHERE plan_id = ANY({0})", f.PlanIds.ToArray());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.payer WHERE payer_id = {0}", f.PayerId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.enrollment_event ENABLE TRIGGER trg_enrollment_event_append_only");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.extract_run ENABLE TRIGGER trg_extract_run_no_delete");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.bulk_job_row ENABLE TRIGGER trg_bulk_job_row_immutable");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.benefit_rule ENABLE TRIGGER trg_benefit_rule_immutable");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.plan_version ENABLE TRIGGER trg_plan_version_immutable");
        }
    }
}
