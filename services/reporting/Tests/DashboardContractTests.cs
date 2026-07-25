using FluentAssertions;
using Mersal.Reporting.Domain;
using Mersal.Reporting.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Reporting.Tests;

/// <summary>Executive-dashboard contract tests (phase 8.3, US-073 accessibility gate): EVERY widget MUST carry an
/// equivalent labelled dataTable AND complete bilingual AR/EN labels — a widget lacking either fails the build. Also
/// proves the zone rule: the financial widget appears only when the caller may read the financial zone, and clinical
/// widgets only when they may read the clinical zone (finance ≠ diagnosis). DB-backed (env-gated) so the widgets are
/// composed exactly as served.</summary>
[Collection("reporting-db")]
public class DashboardContractTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("REPORTING_TEST_DB");

    private static ReportingDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static DashboardBuilder Builder(ReportingDbContext db) =>
        new(new ReportQueries(db, TimeProvider.System), TimeProvider.System);

    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 12, 31);

    [Fact]
    public async Task Every_widget_has_a_data_table_and_bilingual_labels()
    {
        if (Db is null) return;
        await using var db = Ctx();
        var dash = await Builder(db).BuildAsync("t-empty-" + Guid.NewGuid().ToString("N")[..6], From, To, clinical: true, financial: true);

        dash.ContractVersion.Should().Be("1.0");
        dash.Widgets.Should().NotBeEmpty();
        foreach (var w in dash.Widgets)
        {
            w.IsAccessible.Should().BeTrue($"widget '{w.Key}' must have a dataTable + complete bilingual labels");
            w.DataTable.Columns.Should().NotBeEmpty();
            w.DataTable.Columns.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.En) && !string.IsNullOrWhiteSpace(c.Ar));
            w.Title.Ar.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Clinical_and_financial_widgets_are_included_only_when_authorized()
    {
        if (Db is null) return;
        await using var db = Ctx();
        var tenant = "t-empty-" + Guid.NewGuid().ToString("N")[..6];

        var operationalOnly = await Builder(db).BuildAsync(tenant, From, To, clinical: false, financial: false);
        operationalOnly.Widgets.Should().NotContain(w => w.Zone == "clinical");
        operationalOnly.Widgets.Should().NotContain(w => w.Zone == "financial");

        var full = await Builder(db).BuildAsync(tenant, From, To, clinical: true, financial: true);
        full.Widgets.Should().Contain(w => w.Key == "top-diagnoses" && w.Zone == "clinical");
        full.Widgets.Should().Contain(w => w.Key == "financial-summary" && w.Zone == "financial");
    }

    [Fact]
    public void An_incomplete_bilingual_label_fails_the_accessibility_gate()
    {
        // Guards the gate itself: a missing Arabic label must make a widget non-accessible.
        var badTitle = new BiText("Only English", "");
        var table = new DataTable([new("Col", "عمود")], []);
        var w = new DashboardWidget("x", WidgetKind.Bars, "operational", badTitle,
            new BiText("X", "س"), new BiText("Y", "ص"), "count", [], table, From, To, DateTimeOffset.UtcNow);
        w.IsAccessible.Should().BeFalse();
    }
}
