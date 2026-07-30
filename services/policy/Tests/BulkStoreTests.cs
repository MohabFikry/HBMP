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

    /// <summary>
    /// The other half of the replay contract, and the reason the bug above went unseen for so long.
    ///
    /// <para>A replay is skipped only when NOTHING changed. Correcting the contribution and re-uploading is
    /// the single most common fix an operator makes, and it must be APPLIED — a file whose only edit is the
    /// member's share would otherwise be swallowed as "already done" and the correction silently lost.</para>
    ///
    /// <para>This pins the COST-SHARE branch specifically: it stays green even if person-level change
    /// detection is broken, because a changed share alone is enough to make the row an update. The person
    /// branch is pinned separately below — the two failed independently and a single test covering both
    /// would have hidden exactly the defect that got here.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_re_upload_that_only_corrects_the_contribution_is_applied_not_skipped()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);

            var first = await harness.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, f.Beneficiaries));
            await harness.Engine.ValidateAsync(first.JobId, harness.Scope, null);
            (await harness.Engine.CommitAsync(first.JobId, harness.Scope, null)).Job.AppliedRows.Should().Be(3);

            // Same people, same plan, same effective date — so the membership replays on its idempotency key.
            // Only the share differs.
            var corrected = await harness.UploadAsync(
                BulkJobType.MemberEnrolment, EnrolmentCsv(f, f.Beneficiaries, contribution: 35m));
            await harness.Engine.ValidateAsync(corrected.JobId, harness.Scope, null);
            var second = await harness.Engine.CommitAsync(corrected.JobId, harness.Scope, null);

            second.Job.AppliedRows.Should().Be(3, "a replay whose cost share changed is an update, not a no-op");
            second.Job.SkippedRows.Should().Be(0);

            db.ChangeTracker.Clear();
            var shares = await db.Enrollments.AsNoTracking()
                .Where(e => f.Beneficiaries.Contains(e.BeneficiaryId) && !e.IsDeleted)
                .Select(e => e.ContributionPercent).ToListAsync();
            shares.Should().OnlyContain(s => s == 35m, "the correction is the whole point of the re-upload");

            (await db.Enrollments.CountAsync(e => f.Beneficiaries.Contains(e.BeneficiaryId) && !e.IsDeleted))
                .Should().Be(3, "correcting a share must not create a second membership");
        }
        finally { await Cleanup(f); }
    }

    /// <summary>
    /// Person-level change detection, pinned on its own.
    ///
    /// <para>The share is identical here, so <c>costShareChanged</c> is false and the row can only be applied
    /// if the PERSON is seen to have changed. That makes this the test that actually fails when the intake
    /// seam stops noticing edits — the failure mode that shipped: <see cref="BeneficiaryIntake"/> is a record
    /// with an <c>IReadOnlyList</c> member, so <c>==</c> compares that member by reference and every
    /// re-parse looked different. It reported the reverse of the truth on every row, and nothing caught it
    /// because no test asked the question with the share held constant.</para>
    ///
    /// <para>A corrected spelling is not a cosmetic edit for a refugee record — it is how a member whose name
    /// was mis-keyed at intake stops failing identity checks at the desk.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_re_upload_that_only_corrects_a_name_is_applied_not_skipped()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var f = await Seed();
        try
        {
            await using var db = Ctx();
            var harness = new Harness(db, f);

            var first = await harness.UploadAsync(BulkJobType.MemberEnrolment, EnrolmentCsv(f, f.Beneficiaries));
            await harness.Engine.ValidateAsync(first.JobId, harness.Scope, null);
            (await harness.Engine.CommitAsync(first.JobId, harness.Scope, null)).Job.AppliedRows.Should().Be(3);

            // Same plan, same date, SAME SHARE — only the spelling differs, so nothing but person-level
            // change detection can make this an update.
            var corrected = await harness.UploadAsync(
                BulkJobType.MemberEnrolment, EnrolmentCsv(f, f.Beneficiaries, firstName: "Aminah"));
            await harness.Engine.ValidateAsync(corrected.JobId, harness.Scope, null);
            var second = await harness.Engine.CommitAsync(corrected.JobId, harness.Scope, null);

            second.Job.AppliedRows.Should().Be(3, "a corrected name is a real edit the operator came back to make");
            second.Job.SkippedRows.Should().Be(0);

            (await db.Enrollments.CountAsync(e => f.Beneficiaries.Contains(e.BeneficiaryId) && !e.IsDeleted))
                .Should().Be(3, "correcting a name must not create a second membership");
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
            // Row 2 names a plan that does not exist. Aborting the file on it would leave the job
            // half-applied with no record of where it stopped — not atomic, and not accounted for either.
            var csv = new StringBuilder(
                "card_number,first_name,last_name,gender,nationality,phone_no,birthdate,plan,network_tier,contribution\n");
            var rows = new[] { $"{f.Prefix}-Standard", "NO-SUCH-PLAN", $"{f.Prefix}-Standard" };
            for (var i = 0; i < 3; i++)
            {
                csv.Append(CultureInfo.InvariantCulture,
                    $"{CardFor(f.Beneficiaries[i])},Amina{i},Yusuf,Female,SY,+201234567890,1990-01-01,{rows[i]},MERSAL,20\n");
            }

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
            validation.Preview[0].Changes.Should().ContainKey("cardNumber");
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

            // The intake seam is the thing under test in the upsert cases, so it is a real fake rather than a
            // stub: it remembers which cards it has seen, so a second upload of the same file reports the
            // person as unchanged exactly as patient-service would.
            Intake = new RecordingIntake(f.Beneficiaries.ToDictionary(CardFor, id => id));

            IBulkRowApplier[] appliers =
            [
                new MemberEnrolmentApplier(db, Membership, Intake, new SeededTiers(), calendar, clock),
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
        public RecordingIntake Intake { get; }
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

    /// <summary>
    /// Stands in for patient-service's register-or-update-by-card endpoint.
    ///
    /// <para>Deliberately stateful. The whole point of keying on the card is that the SECOND upload of a file
    /// finds the person already there and reports them unchanged; a stub that always answered "created" would
    /// let a broken upsert pass every test.</para>
    /// </summary>
    internal sealed class RecordingIntake(Dictionary<string, Guid> known) : IBeneficiaryIntake
    {
        private readonly Dictionary<string, BeneficiaryIntake> _seen = new(StringComparer.Ordinal);

        public List<string> Cards { get; } = [];

        public Task<BeneficiaryIntakeResult?> UpsertAsync(
            BeneficiaryIntake intake, string? bearerToken, CancellationToken ct = default)
        {
            Cards.Add(intake.CardNumber);
            // An unknown card would be a genuinely new person; the fixtures only ever use seeded ones, so an
            // unseeded card means the CSV and the fixture have drifted apart and the test should say so.
            if (!known.TryGetValue(intake.CardNumber, out var id))
                return Task.FromResult<BeneficiaryIntakeResult?>(null);

            var created = !_seen.ContainsKey(intake.CardNumber);
            var changed = created || !SameContent(_seen[intake.CardNumber], intake);
            _seen[intake.CardNumber] = intake;
            return Task.FromResult<BeneficiaryIntakeResult?>(
                new BeneficiaryIntakeResult(id, "Active", null, created, changed));
        }

        /// <summary>
        /// Compare two intakes by VALUE, which `!=` does not do here.
        ///
        /// <para><see cref="BeneficiaryIntake"/> is a record, so `==` compares its members with
        /// <c>EqualityComparer&lt;T&gt;.Default</c> — and one member, <c>Notes</c>, is an
        /// <c>IReadOnlyList</c>. Lists do not override equality, so that member is compared by REFERENCE and
        /// two separately-parsed copies of the same CSV row are never equal. The fake therefore reported
        /// `Changed: true` for a byte-identical re-upload, which is the opposite of what it exists to
        /// simulate, and the applier duly classified an idempotent replay as Applied rather than Skipped.</para>
        ///
        /// <para>The real seam is patient-service comparing PERSISTED values, so unchanged means unchanged.
        /// Both sides are rebased onto one shared empty list so the reference check on that member passes,
        /// then the notes are compared as a sequence.</para>
        /// </summary>
        private static bool SameContent(BeneficiaryIntake a, BeneficiaryIntake b) =>
            (a with { Notes = NoNotes }) == (b with { Notes = NoNotes }) && a.Notes.SequenceEqual(b.Notes);

        private static readonly IReadOnlyList<(short Slot, string Value)> NoNotes = [];
    }

    /// <summary>The tier catalogue provider-service would return. MERSAL is the one the fixtures enrol onto.</summary>
    private sealed class SeededTiers : INetworkTierCatalog
    {
        private static readonly Guid MersalTier = Guid.Parse("11111111-2222-3333-4444-555555555555");

        public Task<IReadOnlyList<NetworkTierRef>> ActiveTiersAsync(string? bearerToken, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NetworkTierRef>>([new NetworkTierRef(MersalTier, "MERSAL")]);
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

    /// <summary>The card number a seeded beneficiary is reachable by. Derived from the id so the fake intake
    /// and the CSV agree without either having to be told the mapping.</summary>
    private static string CardFor(Guid beneficiaryId) => $"C{beneficiaryId:N}"[..11].ToUpperInvariant();

    private static byte[] EnrolmentCsv(Fixture f, IReadOnlyList<Guid> beneficiaries, DateOnly? from = null,
        decimal contribution = 20m, string firstName = "Amina")
    {
        var effective = from ?? new DateOnly(2026, 2, 1);
        var csv = new StringBuilder(
            "card_number,first_name,last_name,gender,nationality,phone_no,birthdate,plan,network_tier,contribution,effective_from\n");
        var n = 0;
        foreach (var id in beneficiaries)
        {
            n++;
            csv.Append(CultureInfo.InvariantCulture,
                $"{CardFor(id)},{firstName}{n},Yusuf,Female,SY,+201234567890,1990-01-01," +
                $"{f.Prefix}-Standard,MERSAL,{contribution},{effective:yyyy-MM-dd}\n");
        }
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
            PlanVersionId = version.PlanVersionId, PlanLabel = $"{prefix}-Standard",
            EffectiveFrom = new DateOnly(2026, 1, 1), IsDefault = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var enhanced = new PolicyPlan
        {
            PolicyPlanId = Guid.NewGuid(), TenantId = Tenant, PolicyId = policy.PolicyId,
            PlanVersionId = enhancedVersion.PlanVersionId, PlanLabel = $"{prefix}-Enhanced",
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
