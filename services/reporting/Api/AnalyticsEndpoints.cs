using System.Globalization;
using System.Text;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.BenefitPricing;
using Mersal.Reporting.Domain;
using Mersal.Reporting.Infrastructure;
using Mersal.Time;

namespace Mersal.Reporting.Api;

/// <summary>
/// Phase 19.6b — the policy &amp; member analytical dashboard: six views over the pre-aggregated read model,
/// one shared filter vocabulary, compare mode, an audited drill-down and an audited export.
///
/// <para><b>Every view resolves payer scope server-side.</b> The filter bar has a payer control, but that is a
/// narrowing WITHIN what the caller may see — never the thing that decides it. A dashboard is the easiest
/// place in a platform to leak, because a total carries no trace of the rows it was built from.</para>
/// </summary>
public static class AnalyticsEndpoints
{
    public static void MapAnalytics(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/analytics");

        // ── The six views ──────────────────────────────────────────────────────────────────────────────────
        v1.MapGet("/{view}", async (
            string view, HttpRequest request, AnalyticsContext cx, CancellationToken ct) =>
        {
            if (!Enum.TryParse<AnalyticsView>(view, ignoreCase: true, out var parsed))
                return ProblemResults.Invalid("UNKNOWN_VIEW",
                    "view must be one of enrolment|utilization|financial|network|plancomparison|outliers.");

            // The financial and network views are MONEY. They sit behind the financial reporting zone, which
            // the finance role holds and a beneficiary-management officer does not — the same split phase 8.2
            // drew, applied to the same kind of data rather than re-argued per view.
            var zone = parsed is AnalyticsView.Financial or AnalyticsView.Network
                ? ReportingPolicies.ReadFinancial
                : ReportingPolicies.ReadOperational;
            if (await cx.Gate.CheckAsync(zone, ct) is { } denied) return denied;

            var filter = AnalyticsFilterBinding.From(request.Query, cx.Calendar);
            var permitted = await cx.PayersAsync(ct);
            var series = await cx.RunAsync(parsed, filter, permitted, request.Query, ct);

            // Compare mode is opt-in: computing the previous period always would double every view's cost to
            // serve a control most sessions never touch.
            var deltas = Array.Empty<AnalyticsDelta>() as IReadOnlyList<AnalyticsDelta>;
            if (request.Query["compare"] == "1" && filter is { From: not null, To: not null })
            {
                var previous = await cx.RunAsync(parsed, filter.PreviousPeriod(), permitted, request.Query, ct);
                deltas = AnalyticsQueries.Deltas(series, previous, AnalyticsDirection.HigherIsBetter);
            }

            return Results.Ok(new AnalyticsViewResult(
                parsed.ToString(), series, deltas,
                PayerScopeApplied: !permitted.IsUnrestricted,
                Unavailable: []));
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:read"));

        // ── Drill-down: the member rows behind an outlier segment ──────────────────────────────────────────
        //
        // Returns enrolment/beneficiary IDS and figures — never a name. Resolving the person is a separate call
        // to policy/patient with the caller's own token, which writes its own PHI-read audit. Two steps, because
        // a drill list that already carried identities would have made the audit event a formality: the read
        // would have happened at the moment the chart was clicked, for every row, including the ones nobody
        // opened.
        v1.MapGet("/outliers/members", async (
            HttpRequest request, string? band, int? limit, AnalyticsContext cx, CancellationToken ct) =>
        {
            if (await cx.Gate.CheckAsync(ReportingPolicies.ReadOperational, ct) is { } denied) return denied;
            if (!UtilizationBands.TryParse(band, out var parsedBand))
                return ProblemResults.Invalid("UNKNOWN_BAND", "band must be a utilization band.");

            var filter = AnalyticsFilterBinding.From(request.Query, cx.Calendar);
            var permitted = await cx.PayersAsync(ct);
            var rows = await cx.Q.OutlierMembersAsync(cx.Tenant, filter, permitted, parsedBand.ToString(), limit ?? 50, ct);

            // Audited even though no identity is returned: this is the step where a total becomes a list of
            // specific people, and "who asked which members are over their limit" is a question the audit log
            // must be able to answer.
            await cx.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "analytics_drilldown", EntityId = parsedBand.ToString(), Action = AuditAction.Read,
                ActorUserId = cx.Me.Principal?.Subject, TenantId = cx.Tenant,
                DecisionReasonCode = AnalyticsFilterBinding.Describe(filter),
                AfterState = $"rows={rows.Count}", Severity = AuditSeverity.Notice,
                FieldClasses = ["membership"],
            }, ct);

            return Results.Ok(rows);
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:read"));

        // ── Audited export of the CURRENTLY FILTERED view ──────────────────────────────────────────────────
        //
        // The columns are the series the view already returned — the dashboard cannot export a field it does
        // not display, which is what stops it becoming a PHI side-channel with a different column allow-list
        // from the one 19.5b's extract engine enforces.
        v1.MapGet("/{view}/export", async (
            string view, HttpRequest request, AnalyticsContext cx, CancellationToken ct) =>
        {
            if (!Enum.TryParse<AnalyticsView>(view, ignoreCase: true, out var parsed))
                return ProblemResults.Invalid("UNKNOWN_VIEW", "unknown analytics view.");
            if (await cx.Gate.CheckAsync(ReportingPolicies.Export, ct) is { } denied) return denied;

            var filter = AnalyticsFilterBinding.From(request.Query, cx.Calendar);
            var permitted = await cx.PayersAsync(ct);
            var series = await cx.RunAsync(parsed, filter, permitted, request.Query, ct);
            var csv = Csv(series);

            await cx.Audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "analytics_export", EntityId = parsed.ToString(), Action = AuditAction.Export,
                ActorUserId = cx.Me.Principal?.Subject, TenantId = cx.Tenant,
                DecisionReasonCode = AnalyticsFilterBinding.Describe(filter),
                AfterState = $"series={series.Count}", Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Text(csv, "text/csv");
        }).RequireAuthorization(HbmpPolicies.Scope("reporting:export"));
    }

    /// <summary>Long-form CSV: one row per point, with the series it belongs to. Wide-per-series would need a
    /// column per label and produce a ragged file the moment two views disagree on their dimensions.</summary>
    private static string Csv(IReadOnlyList<AnalyticsSeries> series)
    {
        var sb = new StringBuilder("series,label_en,label_ar,value,secondary\n");
        foreach (var s in series)
            foreach (var p in s.Points)
                sb.Append(CultureInfo.InvariantCulture,
                    $"{Field(s.Key)},{Field(p.LabelEn)},{Field(p.LabelAr)},{p.Value.ToString(CultureInfo.InvariantCulture)},{p.Secondary?.ToString(CultureInfo.InvariantCulture) ?? ""}\n");
        return sb.ToString();
    }

    /// <summary>CSV-quote, and neutralise a leading formula character. A label that begins <c>=</c> is executed
    /// by a spreadsheet on open; these labels come from tenant-authored plan and payer names.</summary>
    private static string Field(string s)
    {
        var value = s.Length > 0 && s[0] is '=' or '+' or '-' or '@' ? "'" + s : s;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}

/// <summary>Which direction is GOOD for a given series — supplied to compare mode so a delta chip can say more
/// than "it moved". Enrolment up is good news; cost per member up is not; a chip that renders both the same way
/// has told the reader nothing.</summary>
public static class AnalyticsDirection
{
    public static bool? HigherIsBetter(string key) => key switch
    {
        "membership-movement.joined" => true,
        "membership-movement.net" => true,
        "membership-movement.left" => false,
        "threshold-crossings.over-80" => false,
        "threshold-crossings.over-100" => false,
        "network-split.out-of-network" => false,
        "oon-leakage.leakage-rate" => false,
        "oon-leakage.leakage-cost" => false,
        "cost-per-member-month.pmpm" => false,
        "limit-outliers.over-limit" => false,
        "data-quality.missing-plan" => false,
        "data-quality.missing-group" => false,
        "data-quality.unattributed-payer" => false,
        // Null means "no opinion", and that is the honest default: a shift in the mix of benefit categories or
        // network tiers is neither good nor bad without knowing why, and a chip that guessed would be read as
        // an assessment the platform did not make.
        _ => null,
    };
}

/// <summary>Per-request dependencies for the analytics endpoints, so each handler takes one injected object.</summary>
public sealed class AnalyticsContext(
    ReportingGate gate, AnalyticsQueries q, IPayerDirectory payers,
    IHbmpPrincipalAccessor me, IAuditClient audit, IBusinessCalendar calendar)
{
    public ReportingGate Gate { get; } = gate;
    public AnalyticsQueries Q { get; } = q;
    public IHbmpPrincipalAccessor Me { get; } = me;
    public IAuditClient Audit { get; } = audit;
    public IBusinessCalendar Calendar { get; } = calendar;

    public string Tenant => Me.Principal?.TenantId ?? "";

    /// <summary>Resolve the caller's payer restriction. Fails CLOSED (see <c>HttpPayerDirectory</c>): payer
    /// scope's empty set means unrestricted, so an outage that returned it would widen every dashboard.</summary>
    public async Task<PermittedPayers> PayersAsync(CancellationToken ct) =>
        Me.Principal is { } p ? await payers.GetAsync(p, ct) : PermittedPayers.DenyAll;

    public Task<IReadOnlyList<AnalyticsSeries>> RunAsync(
        AnalyticsView view, AnalyticsFilter filter, PermittedPayers permitted,
        IQueryCollection query, CancellationToken ct) => view switch
    {
        AnalyticsView.Enrolment => Q.EnrolmentAsync(Tenant, filter, permitted, ct),
        AnalyticsView.Utilization => Q.UtilizationAsync(Tenant, filter, permitted, ct),
        AnalyticsView.Financial => Q.FinancialAsync(Tenant, filter, permitted, ct),
        AnalyticsView.Network => Q.NetworkAsync(Tenant, filter, permitted, ct),
        AnalyticsView.PlanComparison => Q.PlanComparisonAsync(
            Tenant, filter, permitted, AnalyticsFilterBinding.PlanIds(query), ct),
        _ => Q.OutliersAsync(Tenant, filter, permitted, ct),
    };
}

/// <summary>
/// Binds the shared filter bar from the query string.
///
/// <para>Every field is URL-encoded so a view is shareable and bookmarkable — which is a REQUIREMENT, not a
/// convenience: "look at this" is how a finding gets escalated, and a link that drops the filters sends the
/// recipient to a different number under the same title. An unparseable value is IGNORED rather than rejected,
/// because a stale bookmark should still open, but a value that parses is applied exactly.</para>
/// </summary>
public static class AnalyticsFilterBinding
{
    public static AnalyticsFilter From(IQueryCollection q, IBusinessCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(calendar);

        var to = Date(q, "to") ?? calendar.Today();
        // A dashboard with no range defaults to the last 30 days rather than all of history: an unbounded first
        // paint is the slow query 19.6b refuses to ship, and "everything ever" is rarely the question.
        var from = Date(q, "from") ?? to.AddDays(-29);

        // An unparseable band is dropped, not rejected: a stale bookmark should still open on the unfiltered
        // view rather than 400 at someone who did nothing wrong.
        var band = UtilizationBands.TryParse(q["band"], out var parsedBand) ? parsedBand : (UtilizationBand?)null;
        return new AnalyticsFilter(
            PayerId: Guid(q, "payerId"),
            PolicyId: Guid(q, "policyId"),
            PolicyPlanId: Guid(q, "policyPlanId"),
            GroupId: Guid(q, "groupId"),
            BranchId: Guid(q, "branchId"),
            NetworkTierCode: Text(q, "tier"),
            BenefitCategoryCode: Text(q, "category"),
            MemberStatus: Text(q, "status"),
            Relationship: Text(q, "relationship"),
            Band: band,
            From: from,
            To: to,
            AsOf: Date(q, "asOf"));
    }

    /// <summary>The plans being compared. More than two is allowed — "is Plan B cheaper" is usually asked about
    /// a shortlist, and forcing pairs would make the officer run the comparison three times.</summary>
    public static IReadOnlyList<Guid> PlanIds(IQueryCollection q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var raw = q["plans"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => System.Guid.TryParse(s, out var g) ? g : System.Guid.Empty)
            .Where(g => g != System.Guid.Empty)];
    }

    /// <summary>A compact, loggable description of the filter — recorded on every export and drill-down audit
    /// event. "Somebody exported the financial view" is not an audit trail; "…for payer X, March, tier B" is.</summary>
    public static string Describe(AnalyticsFilter f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var parts = new List<string>();
        if (f.From is { } from && f.To is { } to) parts.Add($"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
        if (f.AsOf is { } asOf) parts.Add($"asOf={asOf:yyyy-MM-dd}");
        if (f.PayerId is { } payer) parts.Add($"payer={payer}");
        if (f.PolicyId is { } policy) parts.Add($"policy={policy}");
        if (f.PolicyPlanId is { } plan) parts.Add($"plan={plan}");
        if (f.GroupId is { } group) parts.Add($"group={group}");
        if (f.BranchId is { } branch) parts.Add($"branch={branch}");
        if (f.NetworkTierCode is { } tier) parts.Add($"tier={tier}");
        if (f.BenefitCategoryCode is { } cat) parts.Add($"category={cat}");
        if (f.MemberStatus is { } status) parts.Add($"status={status}");
        if (f.Relationship is { } rel) parts.Add($"relationship={rel}");
        if (f.Band is { } band) parts.Add($"band={band}");
        return parts.Count == 0 ? "(no filters)" : string.Join("; ", parts);
    }

    private static Guid? Guid(IQueryCollection q, string key) =>
        System.Guid.TryParse(q[key], out var g) ? g : null;

    private static string? Text(IQueryCollection q, string key) =>
        string.IsNullOrWhiteSpace(q[key]) ? null : q[key].ToString();

    private static DateOnly? Date(IQueryCollection q, string key) =>
        DateOnly.TryParse(q[key], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
