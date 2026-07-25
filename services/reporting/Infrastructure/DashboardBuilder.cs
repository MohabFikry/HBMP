using System.Globalization;
using Mersal.Reporting.Domain;

namespace Mersal.Reporting.Infrastructure;

/// <summary>Composes the executive-dashboard widget contracts (phase 8.3) from the read-model queries. Each widget
/// ships chart series AND its mandatory accessible dataTable AND bilingual AR/EN labels. Zone-tagged so the endpoint
/// includes clinical / financial widgets only for an authorized caller (finance widgets exclude diagnoses by
/// construction — they read the financial fact only). Aggregate + PHI-free throughout.</summary>
public sealed class DashboardBuilder(ReportQueries q, TimeProvider clock)
{
    public const string ContractVersion = "1.0";

    /// <summary>Build the dashboard for the zones the caller may read.</summary>
    public async Task<ExecutiveDashboard> BuildAsync(string tenant, DateOnly from, DateOnly to,
        bool clinical, bool financial, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var widgets = new List<DashboardWidget>
        {
            await TatTrend(tenant, from, to, now, ct),
            await PendingGauge(tenant, from, to, now, ct),
            await WorkloadBars(tenant, from, to, now, ct),
            await UtilizationRanking(tenant, from, to, now, ct),
            await NoShowTrend(tenant, from, to, now, ct),
            await RejectedBreakdown(tenant, from, to, now, ct),
        };
        if (clinical)
        {
            widgets.Add(await TopCodes(tenant, from, to, now, CodeKind.Diagnosis, "top-diagnoses",
                new BiText("Top diagnoses", "أكثر التشخيصات"), ct));
            widgets.Add(await TopCodes(tenant, from, to, now, CodeKind.Medication, "top-medications",
                new BiText("Top medications", "أكثر الأدوية"), ct));
        }
        if (financial)
            widgets.Add(await FinancialSummary(tenant, from, to, now, ct));

        return new ExecutiveDashboard(ContractVersion, now, widgets);
    }

    private async Task<DashboardWidget> TatTrend(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CancellationToken ct)
    {
        var r = await q.ApprovalTatAsync(tenant, f, t, ct);
        var series = new ChartSeries(new BiText("p95 TAT", "زمن الاستجابة (95%)"),
            r.ByPriority.Select(x => new SeriesPoint(x.Dimension, x.P95TatSeconds)).ToList());
        var table = new DataTable(
            [new("Priority", "الأولوية"), new("Count", "العدد"), new("Avg (s)", "المتوسط (ث)"), new("p95 (s)", "95% (ث)"), new("SLA breaches", "تجاوزات المهلة")],
            r.ByPriority.Select(x => (IReadOnlyList<string>)[x.Dimension, S(x.Count), S(x.AvgTatSeconds), S(x.P95TatSeconds), S(x.SlaBreaches)]).ToList());
        return Widget("approval-tat-trend", WidgetKind.Trend, "operational",
            new BiText("Approval turnaround (p95)", "زمن إنجاز الموافقات (95%)"),
            new BiText("Priority", "الأولوية"), new BiText("Seconds", "ثوانٍ"), "seconds", [series], table, f, t, now);
    }

    private async Task<DashboardWidget> PendingGauge(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CancellationToken ct)
    {
        var r = await q.PendingApprovalsAsync(tenant, ct);
        var byStatus = r.Rows.GroupBy(x => x.Status).Select(g => new SeriesPoint(g.Key, g.Sum(x => x.Count))).ToList();
        var series = new ChartSeries(new BiText("Pending", "قيد الانتظار"), byStatus);
        var table = new DataTable(
            [new("Status", "الحالة"), new("Priority", "الأولوية"), new("Age", "المدة"), new("Count", "العدد"), new("SLA breaches", "تجاوزات المهلة")],
            r.Rows.Select(x => (IReadOnlyList<string>)[x.Status, x.Priority, x.AgeBucket, S(x.Count), S(x.SlaBreaches)]).ToList());
        return Widget("pending-approvals-gauge", WidgetKind.Gauge, "operational",
            new BiText("Pending approvals", "الموافقات المعلقة"),
            new BiText("Status", "الحالة"), new BiText("Count", "العدد"), "count", [series], table, f, t, now);
    }

    private async Task<DashboardWidget> WorkloadBars(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CancellationToken ct)
    {
        var r = await q.ClinicWorkloadAsync(tenant, f, t, ct);
        var series = new ChartSeries(new BiText("Encounters", "الزيارات"),
            r.Rows.Select(x => new SeriesPoint($"{x.ClinicId} {x.Period:MM-dd}", x.Encounters)).ToList());
        var table = new DataTable(
            [new("Clinic", "العيادة"), new("Date", "التاريخ"), new("Encounters", "الزيارات")],
            r.Rows.Select(x => (IReadOnlyList<string>)[x.ClinicId, x.Period.ToString("O"), S(x.Encounters)]).ToList());
        return Widget("clinic-workload-bars", WidgetKind.Bars, "operational",
            new BiText("Clinic workload", "حجم العمل بالعيادات"),
            new BiText("Clinic / day", "العيادة / اليوم"), new BiText("Encounters", "الزيارات"), "count", [series], table, f, t, now);
    }

    private async Task<DashboardWidget> UtilizationRanking(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CancellationToken ct)
    {
        var r = await q.UtilizationAsync(tenant, UtilizationDimension.Provider, f, t, ct: ct);
        var series = new ChartSeries(new BiText("Utilization", "الاستخدام"),
            r.Rows.Select(x => new SeriesPoint(x.Code, x.Count)).ToList());
        var table = new DataTable(
            [new("Code", "الرمز"), new("Count", "العدد")],
            r.Rows.Select(x => (IReadOnlyList<string>)[x.Code, S(x.Count)]).ToList());
        return Widget("utilization-by-service-line", WidgetKind.Ranking, "operational",
            new BiText("Utilization by service line", "الاستخدام حسب خط الخدمة"),
            new BiText("Service", "الخدمة"), new BiText("Count", "العدد"), "count", [series], table, f, t, now);
    }

    private async Task<DashboardWidget> NoShowTrend(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CancellationToken ct)
    {
        var r = await q.NoShowAsync(tenant, f, t, ct);
        var series = new ChartSeries(new BiText("No-show rate", "معدل عدم الحضور"),
            r.ByClinic.Select(x => new SeriesPoint(x.ClinicId, x.NoShowRate)).ToList());
        var table = new DataTable(
            [new("Clinic", "العيادة"), new("Booked", "المحجوزة"), new("Attended", "الحاضرة"), new("No-show", "المتغيبة"), new("Rate", "المعدل")],
            r.ByClinic.Select(x => (IReadOnlyList<string>)[x.ClinicId, S(x.Booked), S(x.Attended), S(x.NoShow), S(x.NoShowRate)]).ToList());
        return Widget("no-show-trend", WidgetKind.Trend, "operational",
            new BiText("No-show rate", "معدل عدم الحضور"),
            new BiText("Clinic", "العيادة"), new BiText("Rate", "المعدل"), "ratio", [series], table, f, t, now);
    }

    private async Task<DashboardWidget> RejectedBreakdown(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CancellationToken ct)
    {
        var r = await q.RejectedRequestsAsync(tenant, f, t, ct);
        var series = new ChartSeries(new BiText("Rejections", "حالات الرفض"),
            r.ByReason.Select(x => new SeriesPoint(x.ReasonCode, x.Count)).ToList());
        var table = new DataTable(
            [new("Reason", "السبب"), new("Count", "العدد")],
            r.ByReason.Select(x => (IReadOnlyList<string>)[x.ReasonCode, S(x.Count)]).ToList());
        return Widget("rejected-request-breakdown", WidgetKind.Breakdown, "operational",
            new BiText("Rejected requests", "الطلبات المرفوضة"),
            new BiText("Reason", "السبب"), new BiText("Count", "العدد"), "count", [series], table, f, t, now);
    }

    private async Task<DashboardWidget> TopCodes(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CodeKind kind, string key, BiText title, CancellationToken ct)
    {
        var r = await q.TopCodesAsync(tenant, kind, f, t, ct: ct);
        var series = new ChartSeries(title, r.Rows.Select(x => new SeriesPoint(x.Code, x.Count)).ToList());
        var table = new DataTable(
            [new("Code", "الرمز"), new("Count", "العدد")],
            r.Rows.Select(x => (IReadOnlyList<string>)[x.Code, S(x.Count)]).ToList());
        return Widget(key, WidgetKind.Ranking, "clinical", title,
            new BiText("Code", "الرمز"), new BiText("Count", "العدد"), "count", [series], table, f, t, now);
    }

    private async Task<DashboardWidget> FinancialSummary(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CancellationToken ct)
    {
        var r = await q.FinancialSummaryAsync(tenant, f, t, ct);
        var series = new ChartSeries(new BiText("Value", "القيمة"),
            r.ByServiceLine.Select(x => new SeriesPoint(x.ServiceLine, (double)x.Amount)).ToList());
        var table = new DataTable(
            [new("Service line", "خط الخدمة"), new("Amount", "المبلغ"), new("Count", "العدد")],
            r.ByServiceLine.Select(x => (IReadOnlyList<string>)[x.ServiceLine, x.Amount.ToString(CultureInfo.InvariantCulture), S(x.Count)]).ToList());
        return Widget("financial-summary", WidgetKind.Summary, "financial",
            new BiText("Financial summary", "الملخص المالي"),
            new BiText("Service line", "خط الخدمة"), new BiText("Amount", "المبلغ"), "currency", [series], table, f, t, now);
    }

    private static DashboardWidget Widget(string key, WidgetKind kind, string zone, BiText title, BiText x, BiText y,
        string units, IReadOnlyList<ChartSeries> series, DataTable table, DateOnly f, DateOnly t, DateTimeOffset now) =>
        new(key, kind, zone, title, x, y, units, series, table, f, t, now);

    private static string S(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string S(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
