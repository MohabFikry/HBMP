using FluentAssertions;
using Mersal.Authz;
using Mersal.BenefitPricing;
using Mersal.Reporting.Domain;
using Mersal.Reporting.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Reporting.Tests;

/// <summary>
/// Phase 19.6b — the analytical dashboard's load-bearing claims, against real Postgres
/// (env-gated <c>REPORTING_TEST_DB</c>).
///
/// <para>A dashboard is the easiest surface in a platform to leak from, because a total carries no trace of the
/// rows it was built from: a payer-scoped user shown another payer's members sees a plausible number, not an
/// error. So the scope tests here assert on the AGGREGATE, not on a rendered list.</para>
/// </summary>
[Collection("reporting-db")]
public class AnalyticsTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("REPORTING_TEST_DB");

    private static ReportingDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static readonly DateOnly Day = new(2026, 6, 15);

    // ── Payer scope ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_payer_scoped_user_aggregates_only_their_own_payer()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            db.EnrolmentFacts.AddRange(
                Enrolment(tenant, mine, "Enrolled"), Enrolment(tenant, mine, "Enrolled"),
                Enrolment(tenant, theirs, "Enrolled"), Enrolment(tenant, theirs, "Enrolled"),
                Enrolment(tenant, theirs, "Enrolled"));
            await db.SaveChangesAsync();

            var q = new AnalyticsQueries(db);
            var scoped = await q.EnrolmentAsync(tenant, Filter(), PermittedPayers.RestrictedTo([mine]), default);
            var unrestricted = await q.EnrolmentAsync(tenant, Filter(), PermittedPayers.Unrestricted, default);

            Joined(scoped).Should().Be(2, "a restricted caller's TOTAL must never have included another payer");
            Joined(unrestricted).Should().Be(5);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task A_payer_scoped_user_never_sees_unattributed_rows()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var mine = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            // A pre-19.2 policy with no payer recorded — the rows the 19.7 backfill retires. They belong to
            // SOMEBODY; a caller who asked for one payer's book of business did not ask for "might be anyone".
            db.EnrolmentFacts.AddRange(Enrolment(tenant, mine, "Enrolled"), Enrolment(tenant, null, "Enrolled"));
            await db.SaveChangesAsync();

            var q = new AnalyticsQueries(db);
            Joined(await q.EnrolmentAsync(tenant, Filter(), PermittedPayers.RestrictedTo([mine]), default))
                .Should().Be(1);
            Joined(await q.EnrolmentAsync(tenant, Filter(), PermittedPayers.Unrestricted, default))
                .Should().Be(2, "an unrestricted caller must still see them, or the backfill has nothing to find");
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task DenyAll_is_what_an_admin_service_outage_produces_and_it_shows_nothing()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        try
        {
            await using var db = Ctx();
            db.EnrolmentFacts.Add(Enrolment(tenant, Guid.NewGuid(), "Enrolled"));
            await db.SaveChangesAsync();

            // The failure mode that matters: payer scope's EMPTY set means unrestricted, so an outage that
            // returned it would widen every dashboard on the platform. DenyAll is a restricted set of nobody.
            var q = new AnalyticsQueries(db);
            Joined(await q.EnrolmentAsync(tenant, Filter(), PermittedPayers.DenyAll, default)).Should().Be(0);
        }
        finally { await Cleanup(tenant); }
    }

    // ── The finance invariant ─────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_cost_fact_table_has_no_clinical_column_in_the_live_schema()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        await using var db = Ctx();
        var columns = await db.Database
            .SqlQuery<string>($"SELECT column_name FROM information_schema.columns WHERE table_schema = 'reporting' AND table_name = 'fact_cost'")
            .ToListAsync();

        columns.Should().NotBeEmpty("the migration must have run");
        // Asserted against the SCHEMA, not the entity: a column added by a migration and never mapped would
        // still be readable by anything holding a connection, and the finance role holds one.
        foreach (var forbidden in new[] { "diagnosis", "icd", "icd10", "clinical_note", "note", "chief_complaint" })
            columns.Should().NotContain(c => c.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "the financial view is specified as carrying no diagnosis anywhere");
    }

    // ── Band agreement ────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_dashboard_bands_a_member_exactly_as_policy_query_would()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var payer = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var proj = new AnalyticsProjector(db, TimeProvider.System);
            // 850 of 1,000 — High. The thresholds live in libs/benefit-pricing precisely so this cannot be
            // Medium here and High in a query with both screens looking correct.
            await proj.ProjectAsync(Event(tenant, payer, limit: 1000m, consumed: 850m), Day);
            await db.SaveChangesAsync();

            var fact = await db.MemberUtilizationFacts.AsNoTracking().FirstAsync(f => f.TenantId == tenant);
            fact.Band.Should().Be(UtilizationBands.Of(1000m, 850m, hasCoverage: true).ToString());
            fact.Band.Should().Be(nameof(UtilizationBand.High));
            fact.Remaining.Should().Be(150m);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task An_unbounded_category_reports_no_remaining_rather_than_zero()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        try
        {
            await using var db = Ctx();
            var proj = new AnalyticsProjector(db, TimeProvider.System);
            await proj.ProjectAsync(Event(tenant, Guid.NewGuid(), limit: 0m, consumed: 40m), Day);
            await db.SaveChangesAsync();

            var fact = await db.MemberUtilizationFacts.AsNoTracking().FirstAsync(f => f.TenantId == tenant);
            // Zero remaining would render as "nothing left" on a benefit that was never metered.
            fact.Remaining.Should().BeNull();
            fact.Band.Should().Be(nameof(UtilizationBand.Unlimited));
        }
        finally { await Cleanup(tenant); }
    }

    // ── Idempotency ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_redelivered_membership_event_does_not_double_count_the_member()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        var payer = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var proj = new EventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System),
                new AnalyticsProjector(db, TimeProvider.System));

            var ev = new ReportingEvent(Guid.NewGuid(), "MemberEnrolled", tenant, new Dictionary<string, string>
            {
                ["policyId"] = Guid.NewGuid().ToString(),
                ["payerId"] = payer.ToString(),
                ["beneficiaryId"] = Guid.NewGuid().ToString(),
                ["enrollmentId"] = Guid.NewGuid().ToString(),
                ["relationship"] = "Principal",
                ["status"] = "Active",
            }, new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

            (await proj.ProjectAsync(ev)).Should().BeTrue();
            (await proj.ProjectAsync(ev)).Should().BeFalse("the second delivery is a no-op, not a second member");

            db.ChangeTracker.Clear();
            var count = await db.EnrolmentFacts.AsNoTracking().CountAsync(f => f.TenantId == tenant);
            count.Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    // ── Compare mode ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_delta_states_its_direction_as_a_word_and_whether_that_is_good()
    {
        var current = new[] { Series("membership-movement", ("joined", 120m), ("left", 40m)) };
        var previous = new[] { Series("membership-movement", ("joined", 100m), ("left", 25m)) };

        var deltas = AnalyticsQueries.Deltas(current, previous, Api.AnalyticsDirection.HigherIsBetter);

        var joined = deltas.Single(d => d.Key == "membership-movement.joined");
        joined.Direction.Should().Be("Up", "the four-cue rule needs the TEXT cue in the payload, not a colour");
        joined.PercentChange.Should().Be(20m);
        joined.Better.Should().BeTrue();

        // Direction and desirability are different facts: more members joining is good news, more leaving is not,
        // and a chip that rendered both the same way would have said nothing.
        var left = deltas.Single(d => d.Key == "membership-movement.left");
        left.Direction.Should().Be("Up");
        left.Better.Should().BeFalse();
    }

    [Fact]
    public void The_previous_period_is_the_same_length_immediately_before()
    {
        var filter = new AnalyticsFilter(From: new DateOnly(2026, 3, 1), To: new DateOnly(2026, 3, 31));
        var previous = filter.PreviousPeriod();

        // Not "last calendar month": comparing a 31-day window against February would be a chart that lies by
        // 10%, and it is exactly the chart that ends up in a board pack.
        previous.From.Should().Be(new DateOnly(2026, 1, 29));
        previous.To.Should().Be(new DateOnly(2026, 2, 28));
        (previous.To!.Value.DayNumber - previous.From!.Value.DayNumber)
            .Should().Be(filter.To!.Value.DayNumber - filter.From!.Value.DayNumber);
    }

    // ── The accessible alternative ────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Every_series_carries_its_columns_and_a_bilingual_summary()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = Tenant();
        try
        {
            await using var db = Ctx();
            db.EnrolmentFacts.Add(Enrolment(tenant, Guid.NewGuid(), "Enrolled"));
            await db.SaveChangesAsync();

            var series = await new AnalyticsQueries(db)
                .EnrolmentAsync(tenant, Filter(), PermittedPayers.Unrestricted, default);

            // U6: the data table is not a toggle the client may leave off, so the SERVER always ships what it
            // needs — headers and a one-line summary in both languages. A series that shipped without them
            // would leave the client with nothing to render but the chart.
            series.Should().NotBeEmpty();
            series.Should().OnlyContain(s => s.Columns.Count > 0);
            series.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.SummaryEn));
            series.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.SummaryAr));
            series.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.TitleAr));
        }
        finally { await Cleanup(tenant); }
    }

    // ── Filter round-trip ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_filter_description_names_every_active_narrowing()
    {
        var payer = Guid.NewGuid();
        var filter = new AnalyticsFilter(
            PayerId: payer, NetworkTierCode: "TIER-B", BenefitCategoryCode: "LAB",
            Band: UtilizationBand.High, From: new DateOnly(2026, 3, 1), To: new DateOnly(2026, 3, 31));

        var described = Api.AnalyticsFilterBinding.Describe(filter);

        // "Somebody exported the financial view" is not an audit trail. This string is what makes the event
        // answer "of what".
        described.Should().Contain("2026-03-01..2026-03-31");
        described.Should().Contain($"payer={payer}");
        described.Should().Contain("tier=TIER-B");
        described.Should().Contain("category=LAB");
        described.Should().Contain("band=High");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────

    private static string Tenant() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static AnalyticsFilter Filter() =>
        new(From: new DateOnly(2026, 1, 1), To: new DateOnly(2026, 12, 31));

    private static int Joined(IReadOnlyList<AnalyticsSeries> series) =>
        (int)series.Single(s => s.Key == "membership-movement").Points.Single(p => p.Key == "joined").Value;

    private static EnrolmentFact Enrolment(string tenant, Guid? payer, string movement) => new()
    {
        EventId = Guid.NewGuid(), TenantId = tenant, PayerId = payer, PolicyId = Guid.NewGuid(),
        PolicyPlanId = Guid.NewGuid(), BeneficiaryId = Guid.NewGuid(), EnrollmentId = Guid.NewGuid(),
        Relationship = "Principal", Status = "Active", Movement = movement,
        Period = Day, OccurredAt = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero),
    };

    private static ReportingEvent Event(string tenant, Guid payer, decimal limit, decimal consumed) =>
        new(Guid.NewGuid(), "BenefitConsumed", tenant, new Dictionary<string, string>
        {
            ["policyId"] = Guid.NewGuid().ToString(),
            ["payerId"] = payer.ToString(),
            ["beneficiaryId"] = Guid.NewGuid().ToString(),
            ["enrollmentId"] = Guid.NewGuid().ToString(),
            ["benefitCategoryCode"] = "LAB",
            ["limitValue"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["consumedValue"] = consumed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hasCoverage"] = "true",
        }, new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private static AnalyticsSeries Series(string key, params (string Key, decimal Value)[] points) =>
        new(key, key, key, "count",
            [.. points.Select(p => new AnalyticsPoint(p.Key, p.Key, p.Key, p.Value))],
            "summary", "ملخص", ["A", "B"]);

    private static async Task Cleanup(string tenant)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM reporting.fact_enrolment WHERE tenant_id = {0}", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM reporting.fact_utilization WHERE tenant_id = {0}", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM reporting.fact_cost WHERE tenant_id = {0}", tenant);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM reporting.dim_label WHERE tenant_id = {0}", tenant);
    }
}
