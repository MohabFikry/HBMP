using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.5b — the parts of the bulk engine and the extract engine that are pure: the column contract, the
/// cell rules, the job state machine, the role column allow-list, the schedule grammar and the as-of
/// reconstruction.
///
/// <para>These run without a database on purpose. Every one of them is a rule an operator will meet on a bad
/// day — a mis-titled column, a spreadsheet date, a role that may not see amounts — and rules that only hold
/// when Postgres is up are rules nobody can reason about.</para>
/// </summary>
public class BulkEngineTests
{
    private static readonly BulkFileParser Parser = new();

    // ---- Column contract ---------------------------------------------------------------------------------

    [Fact]
    public void An_unknown_column_fails_the_whole_file_rather_than_being_ignored()
    {
        var csv = Csv("beneficiary_id,policy_no,relationship,effective_from,favourite_colour",
                      $"{Guid.NewGuid()},POL-1,Principal,2026-01-01,blue");

        var result = Parser.Parse(BulkTemplates.MemberEnrolment, "members.csv", csv);

        result.Ok.Should().BeFalse();
        result.Failure!.Code.Should().Be("COLUMN_CONTRACT");
        result.Failure.DetailEn.Should().Contain("favourite_colour");
        // NOTHING is read. A file whose meaning is in dispute must not be half-applied.
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_required_column_fails_the_file_and_names_it()
    {
        // No birthdate — which is required, because every age-banded eligibility rule derives from it.
        var csv = Csv("card_number,first_name,last_name,gender,nationality,phone_no,plan,network_tier,contribution",
                      "A-1,Amina,Yusuf,Female,SY,+201234567890,Mersal,MERSAL,20");

        var result = Parser.Parse(BulkTemplates.MemberEnrolment, "members.csv", csv);

        result.Ok.Should().BeFalse();
        result.Failure!.DetailEn.Should().Contain("birthdate");
        result.Failure.DetailAr.Should().NotBeNullOrWhiteSpace("the people who fix these files work in Arabic");
    }

    [Fact]
    public void Missing_and_unknown_columns_are_reported_together()
    {
        // An operator fixing a header should not discover the second problem only after fixing the first.
        var csv = Csv("card_number,first_name,last_name,favourite_colour", "x,y,z,w");

        var failure = Parser.Parse(BulkTemplates.MemberEnrolment, "members.csv", csv).Failure!;

        failure.DetailEn.Should().Contain("birthdate").And.Contain("favourite_colour");
    }

    [Fact]
    public void Age_is_not_a_column_because_it_is_derived_from_the_birthdate()
    {
        // A file carrying both would eventually carry two different answers, with no rule to choose between
        // them. The header contract refuses the column outright rather than silently ignoring it.
        var csv = Csv("card_number,first_name,last_name,gender,nationality,phone_no,birthdate,plan,network_tier,contribution,age",
                      "A-1,Amina,Yusuf,Female,SY,+201234567890,1990-01-01,Mersal,MERSAL,20,36");

        var failure = Parser.Parse(BulkTemplates.MemberEnrolment, "members.csv", csv).Failure!;

        failure.Code.Should().Be("COLUMN_CONTRACT");
        failure.DetailEn.Should().Contain("age");
    }

    [Fact]
    public void Column_order_and_capitalisation_do_not_matter()
    {
        // Spreadsheets are edited by people; rejecting a file over a capital letter teaches operators to fight
        // the tool rather than read its errors.
        var csv = Csv("Card Number,First Name,LAST_NAME,Gender,NATIONALITY,Phone No,BirthDate,Plan,Network Tier,CONTRIBUTION",
                      "A-1,Amina,Yusuf,Female,SY,+201234567890,1990-01-01,Mersal,MERSAL,20");

        var result = Parser.Parse(BulkTemplates.MemberEnrolment, "members.csv", csv);

        result.Ok.Should().BeTrue();
        result.Rows.Should().HaveCount(1);
        result.Rows[0].Text("card_number").Should().Be("A-1");
        result.Rows[0].Text("birthdate").Should().Be("1990-01-01");
        result.Rows[0].Text("network_tier").Should().Be("MERSAL");
    }

    [Fact]
    public void The_downloadable_template_parses_as_an_empty_job()
    {
        // The template carries a commented legend. If a returned template did not parse, the first thing every
        // operator does — download it, fill it in, upload it — would fail.
        var template = BulkTemplates.MemberTermination.ToCsv();

        var result = Parser.Parse(BulkTemplates.MemberTermination, "t.csv", Encoding.UTF8.GetBytes(template));

        result.Ok.Should().BeTrue();
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Every_template_column_is_described_in_both_locales()
    {
        foreach (var template in BulkTemplates.All)
        {
            template.PurposeAr.Should().NotBeNullOrWhiteSpace();
            foreach (var column in template.Columns)
            {
                column.DescriptionEn.Should().NotBeNullOrWhiteSpace($"{template.JobType}.{column.Name}");
                column.DescriptionAr.Should().NotBeNullOrWhiteSpace($"{template.JobType}.{column.Name}");
            }
        }
    }

    [Fact]
    public void Every_row_error_carries_both_locales()
    {
        RowError[] errors =
        [
            RowError.MissingColumn("member_no"),
            RowError.BadFormat("effective_from", "date"),
            RowError.Unknown("Policy", "POL-9"),
            RowError.OutOfScope("policy POL-9"),
        ];

        errors.Should().OnlyContain(e =>
            !string.IsNullOrWhiteSpace(e.DetailEn) && !string.IsNullOrWhiteSpace(e.DetailAr));
    }

    // ---- Cell rules --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-03-01", 2026, 3, 1)]
    [InlineData("2026/03/01", 2026, 3, 1)]
    public void Dates_parse_only_in_unambiguous_forms(string raw, int y, int m, int d)
    {
        BulkCells.TryDate(raw, out var parsed).Should().BeTrue();
        parsed.Should().Be(new DateOnly(y, m, d));
    }

    [Theory]
    [InlineData("01/03/2026")]
    [InlineData("03/01/2026")]
    [InlineData("1 March 2026")]
    public void A_locale_dependent_date_is_rejected_rather_than_guessed(string raw)
    {
        // dd/mm and mm/dd are indistinguishable, and an enrolment that starts two months early is not a
        // formatting problem — it is somebody covered, or not, on the wrong days.
        BulkCells.TryDate(raw, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("Y", true)]
    [InlineData("1", true)]
    [InlineData("no", false)]
    [InlineData("0", false)]
    public void Booleans_accept_what_people_and_spreadsheets_actually_write(string raw, bool expected)
    {
        BulkCells.TryBool(raw, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Fact]
    public void A_spreadsheet_date_cell_is_read_as_iso_not_as_the_workbooks_culture()
    {
        // The single most dangerous conversion in the file: a real date cell has no unambiguous string form.
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");
        sheet.Cell(1, 1).Value = "member_no";
        sheet.Cell(1, 2).Value = "effective_date";
        sheet.Cell(1, 3).Value = "reason";
        sheet.Cell(2, 1).Value = "MRS-1";
        sheet.Cell(2, 2).Value = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        sheet.Cell(2, 3).Value = "programme ended";
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);

        var result = Parser.Parse(BulkTemplates.MemberTermination, "t.xlsx", ms.ToArray());

        result.Ok.Should().BeTrue();
        result.Rows[0].Text("effective_date").Should().Be("2026-03-01");
    }

    // ---- Idempotency + state machine ---------------------------------------------------------------------

    [Fact]
    public void The_idempotency_key_depends_only_on_the_job_and_the_row_number()
    {
        var job = Guid.NewGuid();

        // Stable across every retry, resume and re-run of the same file — which is what makes "cannot
        // double-apply" true rather than likely.
        BulkIdempotency.KeyFor(job, 4231).Should().Be(BulkIdempotency.KeyFor(job, 4231));
        BulkIdempotency.KeyFor(job, 4231).Should().NotBe(BulkIdempotency.KeyFor(job, 4232));
        BulkIdempotency.KeyFor(Guid.NewGuid(), 4231).Should().NotBe(BulkIdempotency.KeyFor(job, 4231));
    }

    [Fact]
    public void A_job_is_committable_only_from_validated()
    {
        BulkJobTransitions.MayCommit(BulkJobStatus.Validated).Should().BeTrue();
        foreach (var status in Enum.GetValues<BulkJobStatus>().Where(s => s != BulkJobStatus.Validated))
            BulkJobTransitions.MayCommit(status).Should().BeFalse($"a {status} job has not been checked");
    }

    [Fact]
    public void Only_a_completed_job_can_be_rolled_back()
    {
        BulkJobTransitions.MayRollBack(BulkJobStatus.Completed).Should().BeTrue();
        BulkJobTransitions.MayRollBack(BulkJobStatus.Validated).Should().BeFalse();
        BulkJobTransitions.MayRollBack(BulkJobStatus.RolledBack).Should().BeFalse();
    }

    [Fact]
    public void Reconciliation_balances_only_when_every_row_is_accounted_for()
    {
        var job = new BulkJob
        {
            JobId = Guid.NewGuid(), FileName = "f.csv", BatchId = Guid.NewGuid(),
            Status = BulkJobStatus.Completed,
            TotalRows = 10_000, ValidRows = 9_963, InvalidRows = 37,
            AppliedRows = 9_960, FailedRows = 3, SkippedRows = 0,
        };

        job.Balances.Should().BeTrue();

        // A job that cannot say what happened to a row is one that lost it — and the report still renders,
        // which is why the arithmetic is asserted rather than eyeballed.
        job.AppliedRows = 9_000;
        job.Balances.Should().BeFalse();
    }

    // ---- Spreadsheet-safety -------------------------------------------------------------------------------

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1234")]
    [InlineData("-SUM(A1)")]
    [InlineData("@import")]
    public void A_cell_a_spreadsheet_would_evaluate_is_neutralised(string dangerous)
    {
        // Both files this engine emits — the error report and every CSV extract — are opened in a spreadsheet
        // by definition, and a member number is not a formula.
        BulkCsv.Escape(dangerous).Should().StartWith("'");
    }

    // ---- Extract column allow-list ------------------------------------------------------------------------

    [Fact]
    public void A_clinical_column_is_withheld_from_every_role_and_says_so()
    {
        var everything = new ExtractCapabilities(true, true, true, true);

        var resolved = ExtractColumnAllowList.Resolve(ExtractEntity.Members, ["member_no", "diagnosis"], everything);

        resolved.Granted.Should().ContainSingle().Which.Should().Be("member_no");
        var withheld = resolved.Withheld.Should().ContainSingle().Subject;
        withheld.Name.Should().Be("diagnosis");
        withheld.ReasonCode.Should().Be("CLINICAL_NEVER_EXTRACTED");
        withheld.ReasonAr.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_finance_free_role_loses_the_amount_columns_by_name_not_silently()
    {
        var noAmounts = new ExtractCapabilities(Amounts: false, Contract: false, Case: false, Identity: true);

        var resolved = ExtractColumnAllowList.Resolve(
            ExtractEntity.Members, ["member_no", "total_consumed", "policy_no"], noAmounts);

        resolved.Granted.Should().Equal("member_no");
        // NAMED. A spend report missing total_consumed without saying so is not a narrower report, it is a
        // wrong one.
        resolved.Withheld.Select(w => w.Name).Should().BeEquivalentTo(["total_consumed", "policy_no"]);
        resolved.Withheld.Should().OnlyContain(w => w.ReasonCode == "ROLE_NOT_PERMITTED");
    }

    [Fact]
    public void An_unknown_column_is_reported_as_unknown_rather_than_as_a_permission_problem()
    {
        var resolved = ExtractColumnAllowList.Resolve(
            ExtractEntity.Members, ["mmeber_no"], new ExtractCapabilities(true, true, true, true));

        resolved.Withheld.Should().ContainSingle().Which.ReasonCode.Should().Be("UNKNOWN_COLUMN");
    }

    [Fact]
    public void Asking_for_no_columns_gives_the_open_set_not_everything()
    {
        var resolved = ExtractColumnAllowList.Resolve(
            ExtractEntity.Members, null, new ExtractCapabilities(true, true, true, true));

        // A default that included amounts would make the careless request the widest one.
        resolved.Granted.Should().NotContain("total_consumed");
        resolved.Granted.Should().Contain("member_no");
    }

    [Fact]
    public void No_extract_entity_offers_a_clinical_column_a_role_could_ever_be_granted()
    {
        foreach (var entity in Enum.GetValues<ExtractEntity>())
        {
            var clinical = ExtractColumns.For(entity).Where(c => c.Class == ExtractColumnClass.Clinical);
            foreach (var column in clinical)
            {
                var resolved = ExtractColumnAllowList.Resolve(
                    entity, [column.Name], new ExtractCapabilities(true, true, true, true));
                resolved.Granted.Should().BeEmpty($"{entity}.{column.Name} is clinical");
            }
        }
    }

    // ---- Schedule grammar ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("@daily")]
    [InlineData("@weekly")]
    [InlineData("30 2 * * *")]
    public void A_schedule_this_service_can_run_is_accepted(string cron) =>
        ExtractSchedule.TryParse(cron, out _).Should().BeTrue();

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 0 1 1 MON")]
    [InlineData("every tuesday")]
    public void A_schedule_this_service_cannot_run_is_refused_rather_than_stored(string cron) =>
        // Storing an expression we cannot evaluate produces a nightly file that never arrives, discovered
        // months later by whoever was waiting for it.
        ExtractSchedule.TryParse(cron, out _).Should().BeFalse();

    // ---- As-of reconstruction -----------------------------------------------------------------------------

    [Fact]
    public void A_member_terminated_after_the_as_of_date_still_appears()
    {
        var enrollment = Member(from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 3, 15),
            status: EnrollmentStatus.Terminated);

        var state = AsOfMembership.Reconstruct(enrollment,
            [Enrolled(new DateOnly(2026, 1, 1), enrollment.PolicyPlanId),
             Event(EnrollmentEventType.Terminated, new DateOnly(2026, 3, 15))],
            new DateOnly(2026, 3, 1));

        state.WasMember.Should().BeTrue("they were covered on 1 March; the termination applies from 15 March");
        state.Status.Should().Be(EnrollmentStatus.Active);
    }

    [Fact]
    public void A_member_who_changed_plan_after_the_as_of_date_shows_their_earlier_plan()
    {
        var oldPlan = Guid.NewGuid();
        var newPlan = Guid.NewGuid();
        var enrollment = Member(from: new DateOnly(2026, 1, 1), to: null, status: EnrollmentStatus.Active);
        enrollment.PolicyPlanId = newPlan;   // the CURRENT row shows today's plan

        var state = AsOfMembership.Reconstruct(enrollment,
            [Enrolled(new DateOnly(2026, 1, 1), oldPlan),
             Event(EnrollmentEventType.PlanChanged, new DateOnly(2026, 3, 10), newPlan)],
            new DateOnly(2026, 3, 1));

        state.PolicyPlanId.Should().Be(oldPlan);
        state.PlanApproximate.Should().BeFalse("a dated event named the plan");
    }

    [Fact]
    public void A_plan_change_on_or_before_the_as_of_date_is_applied()
    {
        var oldPlan = Guid.NewGuid();
        var newPlan = Guid.NewGuid();
        var enrollment = Member(new DateOnly(2026, 1, 1), null, EnrollmentStatus.Active);
        enrollment.PolicyPlanId = newPlan;

        var state = AsOfMembership.Reconstruct(enrollment,
            [Enrolled(new DateOnly(2026, 1, 1), oldPlan),
             Event(EnrollmentEventType.PlanChanged, new DateOnly(2026, 3, 1), newPlan)],
            new DateOnly(2026, 3, 1));

        state.PolicyPlanId.Should().Be(newPlan);
    }

    [Fact]
    public void A_membership_that_predates_the_dated_events_is_marked_approximate_rather_than_guessed_at()
    {
        var current = Guid.NewGuid();
        var enrollment = Member(new DateOnly(2025, 1, 1), null, EnrollmentStatus.Active);
        enrollment.PolicyPlanId = current;

        // A pre-19.5b Enrolled event carried the plan LABEL and no id.
        var state = AsOfMembership.Reconstruct(enrollment,
            [new AsOfEvent(EnrollmentEventType.Enrolled, new DateOnly(2025, 1, 1), DateTimeOffset.UtcNow)],
            new DateOnly(2026, 3, 1));

        state.PolicyPlanId.Should().Be(current);
        state.PlanApproximate.Should().BeTrue("a reader must be able to tell a reconstructed plan from an assumed one");
    }

    [Fact]
    public void A_cancelled_membership_never_appears_in_an_as_of_list()
    {
        // Cancelled is not "ended" — it is "never happened", which is exactly what a mis-uploaded file makes.
        var enrollment = Member(new DateOnly(2026, 1, 1), null, EnrollmentStatus.Cancelled);

        var state = AsOfMembership.Reconstruct(enrollment,
            [Enrolled(new DateOnly(2026, 1, 1), enrollment.PolicyPlanId)], new DateOnly(2026, 3, 1));

        state.WasMember.Should().BeFalse();
    }

    [Fact]
    public void A_member_enrolled_after_the_as_of_date_does_not_appear()
    {
        var enrollment = Member(new DateOnly(2026, 4, 1), null, EnrollmentStatus.Active);

        AsOfMembership.Reconstruct(enrollment, [], new DateOnly(2026, 3, 1)).WasMember.Should().BeFalse();
    }

    [Fact]
    public void Two_changes_effective_the_same_day_apply_in_the_order_they_were_decided()
    {
        var first = Guid.NewGuid();
        var correction = Guid.NewGuid();
        var enrollment = Member(new DateOnly(2026, 1, 1), null, EnrollmentStatus.Active);

        var state = AsOfMembership.Reconstruct(enrollment,
            [
                Enrolled(new DateOnly(2026, 1, 1), Guid.NewGuid()),
                new AsOfEvent(EnrollmentEventType.PlanChanged, new DateOnly(2026, 2, 1),
                    new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero), first),
                new AsOfEvent(EnrollmentEventType.PlanChanged, new DateOnly(2026, 2, 1),
                    new DateTimeOffset(2026, 2, 1, 14, 0, 0, TimeSpan.Zero), correction),
            ],
            new DateOnly(2026, 3, 1));

        state.PolicyPlanId.Should().Be(correction, "a same-day correction wins over the thing it corrected");
    }

    // ---- Performance smoke --------------------------------------------------------------------------------

    [Fact]
    public void A_fifty_thousand_row_file_parses_in_seconds_not_minutes()
    {
        var csv = new StringBuilder("member_no,effective_date,reason\n");
        for (var i = 0; i < 50_000; i++)
            csv.Append(CultureInfo.InvariantCulture, $"MRS-M-2026-{i:D6},2026-03-01,programme ended\n");

        var stopwatch = Stopwatch.StartNew();
        var result = Parser.Parse(BulkTemplates.MemberTermination, "big.csv", Encoding.UTF8.GetBytes(csv.ToString()));
        stopwatch.Stop();

        result.Ok.Should().BeTrue();
        result.Rows.Should().HaveCount(50_000);
        // Generous by design — this is a smoke test against an accidental O(n²), not a benchmark.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_file_above_the_row_ceiling_is_refused_whole()
    {
        var csv = new StringBuilder("member_no,effective_date,reason\n");
        for (var i = 0; i <= BulkFileParser.MaxRows; i++)
            csv.Append(CultureInfo.InvariantCulture, $"M{i},2026-03-01,r\n");

        var result = Parser.Parse(BulkTemplates.MemberTermination, "huge.csv", Encoding.UTF8.GetBytes(csv.ToString()));

        result.Ok.Should().BeFalse();
        result.Failure!.Code.Should().Be("TOO_MANY_ROWS");
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");

    private static Enrollment Member(DateOnly from, DateOnly? to, EnrollmentStatus status) => new()
    {
        EnrollmentId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(), PolicyId = Guid.NewGuid(),
        PolicyPlanId = Guid.NewGuid(), MemberNo = "MRS-M-2026-000001",
        EffectiveFrom = from, EffectiveTo = to, Status = status,
    };

    private static AsOfEvent Enrolled(DateOnly on, Guid planId) =>
        new(EnrollmentEventType.Enrolled, on, new DateTimeOffset(on, TimeOnly.MinValue, TimeSpan.Zero), planId);

    private static AsOfEvent Event(EnrollmentEventType type, DateOnly on, Guid? planId = null) =>
        new(type, on, new DateTimeOffset(on, TimeOnly.MinValue, TimeSpan.Zero), planId);
}
