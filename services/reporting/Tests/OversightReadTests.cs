using FluentAssertions;
using Mersal.Reporting.Domain;
using Mersal.Reporting.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Reporting.Tests;

/// <summary>
/// The four reads the oversight portal needed and did not have: a clinic with a NAME, the authorizations
/// behind a breach count, claims outcomes for a supervisor, and a dashboard that differs by scope.
/// </summary>
[Collection("reporting-db")]
public class OversightReadTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("REPORTING_TEST_DB");

    private static DbContextOptions<ReportingDbContext> Options() =>
        new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    private static ReportingEvent Ev(string type, string tenant, DateTimeOffset at, params (string, string)[] fields) =>
        new(Guid.NewGuid(), type, tenant, fields.ToDictionary(f => f.Item1, f => f.Item2), at);

    private static EventProjector Projector(ReportingDbContext db) =>
        new(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System), new AnalyticsProjector(db, TimeProvider.System));

    /// <summary>
    /// A labelled clinic reports by name; an unlabelled one reports by id, and neither is "unknown".
    /// </summary>
    /// <remarks>
    /// Both halves in one test on purpose. Asserting only the labelled case would pass on an implementation
    /// that collapsed every unlabelled clinic into a single row — which is exactly what the report did
    /// before, when emr published no location and every fact was written under <c>ClinicId = "unknown"</c>.
    /// One nameless clinic is a gap; every clinic sharing one nameless row is a report that answers nothing
    /// while looking like it answers something.
    /// </remarks>
    [SkippableFact]
    public async Task A_clinic_reports_under_its_name_when_one_has_been_published_and_its_id_otherwise()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);
        var maadi = Guid.NewGuid();
        var unlabelled = Guid.NewGuid();
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = Projector(db);

            await proj.ProjectAsync(Ev(ProjectionMapping.ProjectorEventType("BranchCreated"), tenant, day,
                ("dimensionId", maadi.ToString()), ("kind", "branch"),
                ("labelEn", "Maadi Clinic"), ("labelAr", "عيادة المعادي"), ("code", "MAADI")));

            await proj.ProjectAsync(Ev("EncounterCreated", tenant, day, ("clinicId", maadi.ToString())));
            await proj.ProjectAsync(Ev("EncounterCreated", tenant, day, ("clinicId", unlabelled.ToString())));

            var q = new ReportQueries(db, TimeProvider.System);
            var workload = await q.ClinicWorkloadAsync(tenant, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

            var named = workload.Rows.Single(r => r.ClinicId == maadi.ToString());
            named.ClinicNameEn.Should().Be("Maadi Clinic");
            named.ClinicNameAr.Should().Be("عيادة المعادي");

            var nameless = workload.Rows.Single(r => r.ClinicId == unlabelled.ToString());
            nameless.ClinicNameEn.Should().BeNull("an unlabelled clinic keeps its id rather than borrowing a name");
            workload.Rows.Should().HaveCount(2, "two clinics must not merge into one row for want of a label");
        }
        finally { await CleanupAsync(tenant); }
    }

    /// <summary>A rename reaches the report; the label is not frozen at creation.</summary>
    [SkippableFact]
    public async Task Renaming_a_clinic_changes_what_the_report_calls_it()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);
        var branch = Guid.NewGuid();
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = Projector(db);

            await proj.ProjectAsync(Ev(ProjectionMapping.ProjectorEventType("BranchCreated"), tenant, day,
                ("dimensionId", branch.ToString()), ("kind", "branch"), ("labelEn", "Old Name"), ("labelAr", "الاسم القديم")));
            await proj.ProjectAsync(Ev("EncounterCreated", tenant, day, ("clinicId", branch.ToString())));
            await proj.ProjectAsync(Ev(ProjectionMapping.ProjectorEventType("BranchUpdated"), tenant, day,
                ("dimensionId", branch.ToString()), ("kind", "branch"), ("labelEn", "New Name"), ("labelAr", "الاسم الجديد")));

            var q = new ReportQueries(db, TimeProvider.System);
            var workload = await q.ClinicWorkloadAsync(tenant, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

            workload.Rows.Single().ClinicNameEn.Should().Be("New Name",
                "BranchUpdated carried only the branch code until now, so a rename could reach no reader");
        }
        finally { await CleanupAsync(tenant); }
    }

    /// <summary>
    /// The breach list names the authorizations behind the count, oldest first, and carries no beneficiary.
    /// </summary>
    [SkippableFact]
    public async Task The_breach_list_names_the_authorizations_behind_the_count_oldest_first()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = Projector(db);
            var now = DateTimeOffset.UtcNow;

            async Task SubmitAsync(string authNo, TimeSpan ago, TimeSpan? dueIn) =>
                await proj.ProjectAsync(Ev("AuthSubmitted", tenant, now - ago,
                    ("authorizationId", Guid.NewGuid().ToString()), ("authNo", authNo), ("priority", "Urgent"),
                    ("slaDueAt", (now + (dueIn ?? TimeSpan.Zero)).ToString("O"))));

            await SubmitAsync("AUTH-OLD", TimeSpan.FromDays(4), dueIn: TimeSpan.FromHours(-72));   // breached
            await SubmitAsync("AUTH-RECENT", TimeSpan.FromHours(6), dueIn: TimeSpan.FromHours(-1)); // breached
            await SubmitAsync("AUTH-FINE", TimeSpan.FromHours(1), dueIn: TimeSpan.FromHours(24));   // in time

            var q = new ReportQueries(db, TimeProvider.System);
            var breaches = await q.SlaBreachesAsync(tenant);

            breaches.Total.Should().Be(2, "only the two past their due time are breaches");
            breaches.Rows.Select(r => r.AuthNo).Should().ContainInOrder("AUTH-OLD", "AUTH-RECENT")
                .And.NotContain("AUTH-FINE");

            // The point of the whole drill-down: a number a supervisor can act on, with no patient attached.
            breaches.Rows[0].AgeBucket.Should().Be(">3d");
            typeof(SlaBreachRow).GetProperties().Select(p => p.Name)
                .Should().NotContain(n => n.Contains("Beneficiary", StringComparison.OrdinalIgnoreCase),
                    "this list is a queue worklist, not a patient list");
        }
        finally { await CleanupAsync(tenant); }
    }

    /// <summary>
    /// The claims summary buckets outcomes from the money, so the buckets always sum to the decided total.
    /// </summary>
    [SkippableFact]
    public async Task Claims_summary_derives_the_outcome_from_what_was_actually_allowed()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = Projector(db);

            async Task SettleAsync(string claimed, string approved) =>
                await proj.ProjectAsync(Ev(ProjectionMapping.ProjectorEventType("ClaimApproved.v1"), tenant, day,
                    ("claimedAmount", claimed), ("approvedAmount", approved), ("adjustedAmount", "0.00")));

            await SettleAsync("500.00", "500.00");   // Approved
            await SettleAsync("500.00", "200.00");   // PartiallyApproved
            await SettleAsync("500.00", "0.00");     // Denied

            var q = new ReportQueries(db, TimeProvider.System);
            var summary = await q.ClaimsSummaryAsync(tenant, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

            summary.Decided.Should().Be(3);
            summary.ByOutcome.Sum(r => r.Count).Should().Be(summary.Decided,
                "the buckets are derived from the same rows, so they cannot fail to sum to the total");
            summary.ByOutcome.Single(r => r.Outcome == "Approved").Count.Should().Be(1);
            summary.ByOutcome.Single(r => r.Outcome == "PartiallyApproved").Count.Should().Be(1);
            summary.ByOutcome.Single(r => r.Outcome == "Denied").Count.Should().Be(1);
            summary.TotalAllowed.Should().Be(700.00m, "500 + 200 + 0, net of adjustments");
        }
        finally { await CleanupAsync(tenant); }
    }

    /// <summary>
    /// The three dashboard scopes stop being the same dashboard.
    /// </summary>
    /// <remarks>
    /// Asserted as a DIFFERENCE, not as a fixed widget list. A test naming exactly which widgets a director
    /// gets would have to be edited every time the composition changes and would say nothing about the thing
    /// that was actually broken — that the scope argument never left the browser and all three portals
    /// rendered identical payloads.
    /// </remarks>
    [SkippableFact]
    public async Task A_directors_dashboard_is_not_a_finance_officers_dashboard()
    {
        Skip.If(Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using var db = new ReportingDbContext(Options());
            var builder = new DashboardBuilder(new ReportQueries(db, TimeProvider.System), TimeProvider.System);
            var (from, to) = (new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

            var everything = await builder.BuildAsync(tenant, from, to, clinical: true, financial: true);
            var director = await builder.BuildAsync(tenant, from, to, clinical: true, financial: true, scope: "director");
            var finance = await builder.BuildAsync(tenant, from, to, clinical: true, financial: true, scope: "finance");

            var directorKeys = director.Widgets.Select(w => w.Key).ToList();
            var financeKeys = finance.Widgets.Select(w => w.Key).ToList();

            directorKeys.Should().NotBeEquivalentTo(financeKeys);
            directorKeys.Should().Contain("approval-tat-trend").And.Contain("top-diagnoses");
            financeKeys.Should().NotContain("top-diagnoses",
                "the finance role cannot read the clinical zone, so promising it a diagnosis widget would be "
                + "a promise authorization refuses");
            financeKeys.First().Should().Be("financial-summary", "a scope orders by what its reader opens the page for");

            // A scope narrows and never adds — an unknown one is the full permitted set, which is what every
            // existing caller sending no scope at all continues to receive.
            directorKeys.Should().BeSubsetOf(everything.Widgets.Select(w => w.Key));
            (await builder.BuildAsync(tenant, from, to, true, true, scope: "not-a-scope")).Widgets
                .Should().HaveCount(everything.Widgets.Count);
        }
        finally { await CleanupAsync(tenant); }
    }

    private static async Task CleanupAsync(string tenant)
    {
        if (Db is null) return;
        await using var db = new ReportingDbContext(Options());
        db.EncounterFacts.RemoveRange(await db.EncounterFacts.Where(f => f.TenantId == tenant).ToListAsync());
        db.FinancialFacts.RemoveRange(await db.FinancialFacts.Where(f => f.TenantId == tenant).ToListAsync());
        db.AuthorizationFacts.RemoveRange(await db.AuthorizationFacts.Where(f => f.TenantId == tenant).ToListAsync());
        db.PendingAuthorizations.RemoveRange(await db.PendingAuthorizations.Where(f => f.TenantId == tenant).ToListAsync());
        db.CostFacts.RemoveRange(await db.CostFacts.Where(f => f.TenantId == tenant).ToListAsync());
        db.DimensionLabels.RemoveRange(await db.DimensionLabels.Where(f => f.TenantId == tenant).ToListAsync());
        await db.SaveChangesAsync();
    }
}
