using System.Globalization;
using FluentAssertions;
using Mersal.Reporting.Domain;
using Mersal.Reporting.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Reporting.Tests;

/// <summary>The read-model projections + queries at the datastore (env-gated <c>REPORTING_TEST_DB</c>). Proves
/// US-073: domain events project into aggregate, PHI-free KPI views (approval TAT with p95, pending-approvals
/// snapshot, clinic workload, no-show, utilization, top diagnoses, financial summary); the projection is
/// idempotent (a redelivered event does not double-count); and — the finance invariant — the financial fact table
/// has NO diagnosis column in the live schema. Serialized via the reporting-db collection.</summary>
[Collection("reporting-db")]
public class ProjectionTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("REPORTING_TEST_DB");

    private static DbContextOptions<ReportingDbContext> Options() =>
        new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    private static ReportingEvent Ev(string type, string tenant, DateTimeOffset at, params (string, string)[] fields) =>
        new(Guid.NewGuid(), type, tenant, fields.ToDictionary(f => f.Item1, f => f.Item2), at);

    [SkippableFact]
    public async Task Approval_decisions_project_into_tat_with_p95_and_breach_counts()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = new EventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System), new AnalyticsProjector(db, TimeProvider.System));
            // three approved with TATs 100/200/900, one breaching SLA
            await proj.ProjectAsync(Ev("AuthApproved", tenant, day, ("authNo", "AUTH-1"), ("priority", "Urgent"), ("tatSeconds", "100"), ("slaBreached", "false")));
            await proj.ProjectAsync(Ev("AuthApproved", tenant, day, ("authNo", "AUTH-2"), ("priority", "Urgent"), ("tatSeconds", "200"), ("slaBreached", "false")));
            await proj.ProjectAsync(Ev("AuthApproved", tenant, day, ("authNo", "AUTH-3"), ("priority", "Urgent"), ("tatSeconds", "900"), ("slaBreached", "true")));

            var q = new ReportQueries(db, TimeProvider.System);
            var tat = await q.ApprovalTatAsync(tenant, new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 31));
            tat.Total.Should().Be(3);
            tat.AvgTatSeconds.Should().BeApproximately(400, 0.5);
            tat.P95TatSeconds.Should().Be(900);
            tat.SlaBreaches.Should().Be(1);
            tat.ByPriority.Single(r => r.Dimension == "Urgent").Count.Should().Be(3);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Pending_snapshot_tracks_in_flight_and_drops_on_decision()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var authId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = new EventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System), new AnalyticsProjector(db, TimeProvider.System));
            await proj.ProjectAsync(Ev("AuthSubmitted", tenant, now.AddHours(-30), ("authorizationId", authId.ToString()), ("priority", "Routine")));

            var q = new ReportQueries(db, TimeProvider.System);
            var pending = await q.PendingApprovalsAsync(tenant);
            pending.Total.Should().Be(1);
            pending.Rows.Single().AgeBucket.Should().Be("1-3d");   // submitted 30h ago
            pending.Rows.Single().Status.Should().Be("Submitted");

            // Decision removes it from the pending snapshot.
            await proj.ProjectAsync(Ev("AuthApproved", tenant, now, ("authorizationId", authId.ToString()), ("authNo", "AUTH-9"), ("priority", "Routine"), ("tatSeconds", "10")));
            (await q.PendingApprovalsAsync(tenant)).Total.Should().Be(0);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Projection_is_idempotent_a_redelivered_event_does_not_double_count()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = DateTimeOffset.UtcNow;
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = new EventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System), new AnalyticsProjector(db, TimeProvider.System));
            var ev = Ev("DiagnosisRecorded", tenant, day, ("icd", "E11.9"));
            (await proj.ProjectAsync(ev)).Should().BeTrue();
            (await proj.ProjectAsync(ev)).Should().BeFalse();   // redelivery

            var q = new ReportQueries(db, TimeProvider.System);
            var top = await q.TopCodesAsync(tenant, CodeKind.Diagnosis, DateOnly.FromDateTime(day.UtcDateTime.AddDays(-1)), DateOnly.FromDateTime(day.UtcDateTime.AddDays(1)));
            top.Rows.Single(r => r.Code == "E11.9").Count.Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task No_show_and_workload_project_from_appointment_and_encounter_events()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = new EventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System), new AnalyticsProjector(db, TimeProvider.System));
            await proj.ProjectAsync(Ev("EncounterCreated", tenant, day, ("clinicId", "C1")));
            await proj.ProjectAsync(Ev("EncounterCreated", tenant, day, ("clinicId", "C1")));
            await proj.ProjectAsync(Ev("AppointmentBooked", tenant, day, ("clinicId", "C1")));
            await proj.ProjectAsync(Ev("AppointmentAttended", tenant, day, ("clinicId", "C1")));
            await proj.ProjectAsync(Ev("AppointmentNoShow", tenant, day, ("clinicId", "C1")));

            var q = new ReportQueries(db, TimeProvider.System);
            var wl = await q.ClinicWorkloadAsync(tenant, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
            wl.Rows.Single(r => r.ClinicId == "C1").Encounters.Should().Be(2);

            var ns = await q.NoShowAsync(tenant, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
            ns.NoShow.Should().Be(1);
            ns.Attended.Should().Be(1);
            ns.NoShowRate.Should().BeApproximately(0.5, 0.001);   // 1 no-show / (1 attended + 1 no-show)
        }
        finally { await Cleanup(tenant); }
    }

    /// <summary>
    /// The financial summary is built from SETTLED CLAIM LINES, and the money is what was allowed.
    ///
    /// <para>This test used to project <c>ServiceValued</c>, an event no service on the platform publishes —
    /// so it proved the projector could handle a message that never arrived, while
    /// <c>/reports/financial-summary</c> and the executive dashboard's financial widget returned zero in
    /// production from the day they were written. The fixture was the only thing that ever fed that table.</para>
    /// </summary>
    [SkippableFact]
    public async Task Financial_summary_projects_from_settled_claim_lines()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = new EventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System), new AnalyticsProjector(db, TimeProvider.System));

            // Two lab lines and one radiology line, as claims publishes them: the published name carries the
            // `.v1` suffix and is translated by ProjectionMapping, so projecting the raw name here would
            // exercise a path the queue never takes.
            async Task LineAsync(string line, string code, string amount) =>
                await proj.ProjectAsync(Ev(ProjectionMapping.ProjectorEventType("ClaimLineSettled.v1"), tenant, day,
                    ("serviceLine", line), ("serviceCode", code), ("amount", amount)));

            await LineAsync("Lab", "80053", "150.00");
            await LineAsync("Lab", "80053", "50.00");
            await LineAsync("Radiology", "71046", "400.00");

            var q = new ReportQueries(db, TimeProvider.System);
            var fin = await q.FinancialSummaryAsync(tenant, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

            fin.TotalAmount.Should().Be(600.00m);
            fin.ByServiceLine.Single(r => r.ServiceLine == "Lab").Amount.Should().Be(200.00m);
            fin.ByServiceLine.Single(r => r.ServiceLine == "Lab").Count.Should().Be(2);
            fin.ByServiceLine.Single(r => r.ServiceLine == "Radiology").Amount.Should().Be(400.00m);
        }
        finally { await Cleanup(tenant); }
    }

    /// <summary>
    /// The claim-level settlement and the per-line settlement are different grains, and only one of them is
    /// a financial fact.
    /// </summary>
    /// <remarks>
    /// Both events fire for the same claim, in the same transaction, describing the same money.
    /// <c>ClaimSettled</c> feeds <c>fact_cost</c> (one row per claim, with the payer/tier axes) and
    /// <c>ClaimLineSettled</c> feeds <c>financial_fact</c> (one row per service line). If either projector
    /// case ever learned the other's event, every settled claim would be counted twice and the financial
    /// summary would quietly double — the kind of error that looks like growth.
    /// </remarks>
    [SkippableFact]
    public async Task A_claim_level_settlement_is_not_also_a_service_line_fact()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
        var day = new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);
        try
        {
            await using var db = new ReportingDbContext(Options());
            var proj = new EventProjector(db, TimeProvider.System, new BusinessCalendar(TimeProvider.System), new AnalyticsProjector(db, TimeProvider.System));

            await proj.ProjectAsync(Ev(ProjectionMapping.ProjectorEventType("ClaimApproved.v1"), tenant, day,
                ("claimedAmount", "600.00"), ("approvedAmount", "600.00"), ("adjustedAmount", "0.00")));
            await proj.ProjectAsync(Ev(ProjectionMapping.ProjectorEventType("ClaimLineSettled.v1"), tenant, day,
                ("serviceLine", "Lab"), ("serviceCode", "80053"), ("amount", "600.00")));

            var q = new ReportQueries(db, TimeProvider.System);
            var fin = await q.FinancialSummaryAsync(tenant, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

            fin.TotalAmount.Should().Be(600.00m, "the claim-level event is a cost fact, not a service-line fact");
            fin.ByServiceLine.Should().HaveCount(1);

            // And the claim-level event did land where it belongs, so this is not passing because nothing
            // was projected at all.
            (await db.CostFacts.AsNoTracking().CountAsync(c => c.TenantId == tenant)).Should().Be(1);
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Financial_fact_table_has_no_diagnosis_column_in_the_live_schema()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = new ReportingDbContext(Options());
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT string_agg(column_name, ',') FROM information_schema.columns
                                WHERE table_schema='reporting' AND table_name='financial_fact';";
            var cols = ((string?)await cmd.ExecuteScalarAsync() ?? "").ToLower(CultureInfo.InvariantCulture);
            cols.Should().NotBeNullOrEmpty();
            cols.Should().NotContainAny("diagnosis", "icd", "clinical", "note", "result");
        }
        finally { await conn.CloseAsync(); }
    }

    private static async Task Cleanup(string tenant)
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = new ReportingDbContext(Options());
        // gather base event ids (processed_event holds only base ids) then remove facts by tenant.
        var af = await db.AuthorizationFacts.Where(f => f.TenantId == tenant).ToListAsync();
        var ef = await db.EncounterFacts.Where(f => f.TenantId == tenant).ToListAsync();
        var uf = await db.UtilizationFacts.Where(f => f.TenantId == tenant).ToListAsync();
        var cc = await db.CodeCounts.Where(f => f.TenantId == tenant).ToListAsync();
        var ff = await db.FinancialFacts.Where(f => f.TenantId == tenant).ToListAsync();
        var pa = await db.PendingAuthorizations.Where(f => f.TenantId == tenant).ToListAsync();
        db.AuthorizationFacts.RemoveRange(af); db.EncounterFacts.RemoveRange(ef); db.UtilizationFacts.RemoveRange(uf);
        db.CodeCounts.RemoveRange(cc); db.FinancialFacts.RemoveRange(ff); db.PendingAuthorizations.RemoveRange(pa);
        await db.SaveChangesAsync();
        // processed_event rows for this tenant's events: match on the fact event ids we saw.
        var ids = af.Select(x => x.EventId).Concat(ef.Select(x => x.EventId)).Concat(ff.Select(x => x.EventId))
            .Concat(cc.Select(x => x.EventId)).Concat(uf.Select(x => x.EventId)).Distinct().ToList();
        var pe = await db.ProcessedEvents.Where(p => ids.Contains(p.EventId)).ToListAsync();
        db.ProcessedEvents.RemoveRange(pe);
        await db.SaveChangesAsync();
    }
}
