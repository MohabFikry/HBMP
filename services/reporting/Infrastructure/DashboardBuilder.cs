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

    /// <summary>
    /// Which widgets each dashboard SCOPE is about.
    /// </summary>
    /// <remarks>
    /// <para>Distinct from the zone check, and the distinction matters. The zone decides what a caller MAY
    /// see and is enforced in authorization; the scope decides what their dashboard is FOR, and narrows
    /// within what they may see. A Medical Director and a Finance officer can both read the cost widget, and
    /// only one of them opens a dashboard to look at it first.</para>
    /// <para>Before this existed the scope was a client-side argument that never left the browser: the SPA
    /// took "executive" | "finance" | "director", sent all three to the same URL, and rendered byte-identical
    /// payloads under three different headings.</para>
    /// </remarks>
    private static readonly Dictionary<string, string[]> ScopeWidgets = new(StringComparer.OrdinalIgnoreCase)
    {
        // Clinical oversight: the queue, the clinics, and what the clinics did. Cost is present because the
        // director holds the financial zone and absorbs the consequence of what they approve — but it is the
        // last widget rather than the first, and the detail lives in Claims & Cost.
        ["director"] = ["approval-tat-trend", "pending-approvals-gauge", "clinic-workload-bars", "no-show-trend",
                        "rejected-request-breakdown", "utilization-by-provider", "top-diagnoses", "top-medications",
                        "financial-summary"],
        /*
         * Money first, and then everything else finance may already see.
         *
         * ORDERING, NOT REMOVAL, and the distinction is deliberate. The finance dashboard is outside the
         * scope of the 2026-08-11 oversight audit, and dropping widgets a finance officer opens every
         * morning would be a behaviour change nobody asked for — so every operational key is listed, just
         * behind the cost ones. The two clinical keys are absent because the finance role cannot read that
         * zone at all: the zone check above already excludes them, and listing them would be a promise the
         * authorization layer refuses.
         */
        ["finance"] = ["financial-summary", "utilization-by-provider", "rejected-request-breakdown",
                       "approval-tat-trend", "pending-approvals-gauge", "clinic-workload-bars", "no-show-trend"],
    };

    /// <summary>Build the dashboard for the zones the caller may read, narrowed to what their scope is about.</summary>
    public async Task<ExecutiveDashboard> BuildAsync(string tenant, DateOnly from, DateOnly to,
        bool clinical, bool financial, string? scope = null, CancellationToken ct = default)
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

        // An unknown or absent scope keeps everything the zones allowed — the executive dashboard, and the
        // behaviour every existing caller already has. A scope narrows; it never adds.
        // Ordered by the scope's own list, not by build order: the array IS the priority, so the widget a
        // director opens the page to see is the one at the top of theirs.
        if (scope is not null && ScopeWidgets.TryGetValue(scope, out var wanted))
            widgets = [.. widgets.Where(w => wanted.Contains(w.Key, StringComparer.Ordinal))
                                 .OrderBy(w => Array.IndexOf(wanted, w.Key))];

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
            r.Rows.Select(x => new SeriesPoint($"{Clinic(x.ClinicId, x.ClinicNameEn)} {x.Period:MM-dd}", x.Encounters)).ToList());
        var table = new DataTable(
            [new("Clinic", "العيادة"), new("Date", "التاريخ"), new("Encounters", "الزيارات")],
            r.Rows.Select(x => (IReadOnlyList<string>)[Clinic(x.ClinicId, x.ClinicNameEn), x.Period.ToString("O"), S(x.Encounters)]).ToList());
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
        // NAMED FOR WHAT IT RANKS. This was keyed `utilization-by-service-line` and titled "Utilization by
        // service line" while querying `UtilizationDimension.Provider` — so it ranked PROVIDERS under a
        // heading promising service lines, and a supervisor reading it drew conclusions about the wrong axis.
        // The other three dimensions (drug, lab, radiology) are reachable from the Utilization section, which
        // is where a question about which axis belongs.
        return Widget("utilization-by-provider", WidgetKind.Ranking, "operational",
            new BiText("Utilization by provider", "الاستخدام حسب مقدم الخدمة"),
            new BiText("Provider", "مقدم الخدمة"), new BiText("Count", "العدد"), "count", [series], table, f, t, now);
    }

    private async Task<DashboardWidget> NoShowTrend(string tenant, DateOnly f, DateOnly t, DateTimeOffset now, CancellationToken ct)
    {
        var r = await q.NoShowAsync(tenant, f, t, ct);
        var series = new ChartSeries(new BiText("No-show rate", "معدل عدم الحضور"),
            r.ByClinic.Select(x => new SeriesPoint(Clinic(x.ClinicId, x.ClinicNameEn), x.NoShowRate)).ToList());
        var table = new DataTable(
            [new("Clinic", "العيادة"), new("Booked", "المحجوزة"), new("Attended", "الحاضرة"), new("No-show", "المتغيبة"), new("Rate", "المعدل")],
            r.ByClinic.Select(x => (IReadOnlyList<string>)[Clinic(x.ClinicId, x.ClinicNameEn), S(x.Booked), S(x.Attended), S(x.NoShow), S(x.NoShowRate)]).ToList());
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

    /// <summary>
    /// A clinic's display name, or its id when nothing has labelled it.
    /// </summary>
    /// <remarks>
    /// The FALLBACK is the point. A location that predates the branch-label feed has no name, and showing a
    /// blank or "unknown" there would merge every unlabelled clinic into one row — which is what the report
    /// did before, for every clinic, and it read as a real answer. An id is ugly and it is honest.
    /// <para>English only in this widget's series and table: these are the chart AXIS labels, which the SPA
    /// renders through <c>neutral()</c> rather than translating. The bilingual pair travels on the report
    /// contract itself, which is where a screen that can switch language reads it from.</para>
    /// </remarks>
    private static string Clinic(string clinicId, string? nameEn) =>
        string.IsNullOrWhiteSpace(nameEn) ? clinicId : nameEn;

    private static string S(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string S(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
