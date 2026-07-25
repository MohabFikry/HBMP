namespace Mersal.Reporting.Domain;

// ── Executive-dashboard data contracts (phase 8.3, US-073). Versioned, aggregate/PHI-free. EVERY chart widget
// carries an equivalent labelled dataTable (WCAG non-visual equivalent) AND bilingual AR/EN labels, so the front
// end can render a localized, RTL-correct accessible table for each chart. A widget with no dataTable is invalid.

/// <summary>A bilingual label — both are authored (Arabic RTL, never machine-translated at render time).</summary>
public sealed record BiText(string En, string Ar)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(En) && !string.IsNullOrWhiteSpace(Ar);
}

/// <summary>One point in a chart series.</summary>
public sealed record SeriesPoint(string Label, double Value);

/// <summary>A named chart series.</summary>
public sealed record ChartSeries(BiText Name, IReadOnlyList<SeriesPoint> Points);

/// <summary>The accessible data-table alternative every chart MUST carry: labelled column headers + rows.</summary>
public sealed record DataTable(IReadOnlyList<BiText> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>The kind of widget the front end renders (chart shape hint) — data is identical regardless.</summary>
public enum WidgetKind { Trend, Bars, Gauge, Ranking, Breakdown, Summary }

/// <summary>A single dashboard widget payload: chart series + the mandatory dataTable + bilingual axis/title labels +
/// units, period and refresh time. Zone-tagged so the composer includes it only for an authorized caller.</summary>
public sealed record DashboardWidget(
    string Key,
    WidgetKind Kind,
    string Zone,                 // operational | clinical | financial
    BiText Title,
    BiText XAxis,
    BiText YAxis,
    string Units,
    IReadOnlyList<ChartSeries> Series,
    DataTable DataTable,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateTimeOffset LastRefreshedAt)
{
    /// <summary>A widget is valid iff it has a dataTable (columns + at least the header) and complete bilingual
    /// labels — the phase-8.3 CI acceptance gate.</summary>
    public bool IsAccessible =>
        DataTable is { Columns.Count: > 0 }
        && DataTable.Columns.All(c => c.IsComplete)
        && Title.IsComplete && XAxis.IsComplete && YAxis.IsComplete
        && Series.All(s => s.Name.IsComplete);
}

/// <summary>The versioned executive-dashboard contract returned by <c>GET /dashboards/executive</c>.</summary>
public sealed record ExecutiveDashboard(string ContractVersion, DateTimeOffset GeneratedAt, IReadOnlyList<DashboardWidget> Widgets);
