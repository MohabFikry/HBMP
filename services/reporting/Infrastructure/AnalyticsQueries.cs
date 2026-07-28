using System.Globalization;
using Mersal.Authz;
using Mersal.BenefitPricing;
using Mersal.Reporting.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Reporting.Infrastructure;

/// <summary>
/// Phase 19.6b — the six dashboard views, each a query over the pre-aggregated facts.
///
/// <para>Every method takes the caller's <see cref="PermittedPayers"/> and applies it as a PREDICATE, before
/// any grouping. Scope is not a filter the client sets and it is not a post-filter on the rendered rows:
/// a payer-restricted user's totals must never have included another payer's data even transiently, because
/// a total is exactly the shape in which a leak is invisible.</para>
/// </summary>
public sealed class AnalyticsQueries(ReportingDbContext db)
{
    // ── Scoping ───────────────────────────────────────────────────────────────────────────────────────────

    // Each fact type gets its own predicate builder below rather than one generic helper: EF cannot translate
    // a `Func<T, Guid?>` selector into SQL, so a "shared" version would have had to materialise the rows first
    // and filter in memory — a payer-scoped user's query would then have READ every payer's facts before
    // discarding them, which is the same leak the scope exists to prevent, just further downstream.
    //
    // The rule they all apply: a payer-restricted caller never sees unattributed rows (payer_id NULL — the
    // pre-19.2 policies the 19.7 backfill retires). They asked for one payer's book of business, and a row
    // that might belong to any payer is not it.

    private IQueryable<EnrolmentFact> Enrolments(string tenant, AnalyticsFilter f, PermittedPayers p)
    {
        var q = db.EnrolmentFacts.AsNoTracking().Where(x => x.TenantId == tenant);
        if (!p.IsUnrestricted)
            q = q.Where(x => x.PayerId != null && p.PayerIds.Contains(x.PayerId.Value));
        if (f.PayerId is { } payer) q = q.Where(x => x.PayerId == payer);
        if (f.PolicyId is { } pol) q = q.Where(x => x.PolicyId == pol);
        if (f.PolicyPlanId is { } plan) q = q.Where(x => x.PolicyPlanId == plan);
        if (f.GroupId is { } grp) q = q.Where(x => x.GroupId == grp);
        if (f.BranchId is { } br) q = q.Where(x => x.BranchId == br);
        if (!string.IsNullOrWhiteSpace(f.MemberStatus)) q = q.Where(x => x.Status == f.MemberStatus);
        if (!string.IsNullOrWhiteSpace(f.Relationship)) q = q.Where(x => x.Relationship == f.Relationship);
        if (f.From is { } from) q = q.Where(x => x.Period >= from);
        if (f.To is { } to) q = q.Where(x => x.Period <= to);
        if (f.AsOf is { } asOf) q = q.Where(x => x.Period <= asOf);
        return q;
    }

    private IQueryable<MemberUtilizationFact> Utilizations(string tenant, AnalyticsFilter f, PermittedPayers p)
    {
        var q = db.MemberUtilizationFacts.AsNoTracking().Where(x => x.TenantId == tenant);
        if (!p.IsUnrestricted)
            q = q.Where(x => x.PayerId != null && p.PayerIds.Contains(x.PayerId.Value));
        if (f.PayerId is { } payer) q = q.Where(x => x.PayerId == payer);
        if (f.PolicyId is { } pol) q = q.Where(x => x.PolicyId == pol);
        if (f.PolicyPlanId is { } plan) q = q.Where(x => x.PolicyPlanId == plan);
        if (f.GroupId is { } grp) q = q.Where(x => x.GroupId == grp);
        if (f.BranchId is { } br) q = q.Where(x => x.BranchId == br);
        if (!string.IsNullOrWhiteSpace(f.BenefitCategoryCode)) q = q.Where(x => x.BenefitCategoryCode == f.BenefitCategoryCode);
        if (!string.IsNullOrWhiteSpace(f.NetworkTierCode)) q = q.Where(x => x.NetworkTierCode == f.NetworkTierCode);
        if (f.Band is { } band) q = q.Where(x => x.Band == band.ToString());
        if (f.From is { } from) q = q.Where(x => x.Period >= from);
        if (f.To is { } to) q = q.Where(x => x.Period <= to);
        if (f.AsOf is { } asOf) q = q.Where(x => x.Period <= asOf);
        return q;
    }

    private IQueryable<CostFact> Costs(string tenant, AnalyticsFilter f, PermittedPayers p)
    {
        var q = db.CostFacts.AsNoTracking().Where(x => x.TenantId == tenant);
        if (!p.IsUnrestricted)
            q = q.Where(x => x.PayerId != null && p.PayerIds.Contains(x.PayerId.Value));
        if (f.PayerId is { } payer) q = q.Where(x => x.PayerId == payer);
        if (f.PolicyId is { } pol) q = q.Where(x => x.PolicyId == pol);
        if (f.PolicyPlanId is { } plan) q = q.Where(x => x.PolicyPlanId == plan);
        if (!string.IsNullOrWhiteSpace(f.BenefitCategoryCode)) q = q.Where(x => x.BenefitCategoryCode == f.BenefitCategoryCode);
        if (!string.IsNullOrWhiteSpace(f.NetworkTierCode)) q = q.Where(x => x.NetworkTierCode == f.NetworkTierCode);
        if (f.From is { } from) q = q.Where(x => x.Period >= from);
        if (f.To is { } to) q = q.Where(x => x.Period <= to);
        if (f.AsOf is { } asOf) q = q.Where(x => x.Period <= asOf);
        return q;
    }

    // ── 1. Enrolment ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AnalyticsSeries>> EnrolmentAsync(
        string tenant, AnalyticsFilter f, PermittedPayers p, CancellationToken ct)
    {
        var q = Enrolments(tenant, f, p);

        var byMovement = await q.GroupBy(x => x.Movement)
            .Select(g => new { Movement = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Churn is the pair, not either half. "42 joined" is a recruitment number; "42 joined, 51 left" is the
        // membership story, and reporting only the first is how a shrinking programme reads as a growing one.
        var joined = byMovement.Where(m => m.Movement is "Enrolled" or "Reinstated").Sum(m => m.Count);
        var left = byMovement.Where(m => m.Movement is "Terminated" or "Cancelled").Sum(m => m.Count);

        var byRelationship = await q.Where(x => x.Movement == "Enrolled")
            .GroupBy(x => x.Relationship)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byPlan = await q.Where(x => x.Movement == "Enrolled" && x.PolicyPlanId != null)
            .GroupBy(x => x.PolicyPlanId!.Value)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var waiting = await q.CountAsync(x => x.InWaitingPeriod, ct);
        var labels = await LabelsAsync(tenant, "policy_plan", ct);

        return
        [
            Series("membership-movement", "Membership movement", "حركة العضوية", "count",
            [
                Point("joined", "Joined", "انضم", joined),
                Point("left", "Left", "غادر", left),
                Point("net", "Net change", "صافي التغيّر", joined - left),
            ], ["Movement", "Members"]),

            Series("by-relationship", "Enrolments by relationship", "التسجيلات حسب صلة القرابة", "count",
                [.. byRelationship.Select(r => Point(r.Key, r.Key, r.Key, r.Count))],
                ["Relationship", "Members"]),

            Series("by-plan", "Enrolments by plan", "التسجيلات حسب الخطة", "count",
                [.. byPlan.Select(r => Point(r.Key.ToString(), Label(labels, r.Key, en: true), Label(labels, r.Key, en: false), r.Count, r.Key))],
                ["Plan", "Members"]),

            // Its own series because it is an OPERATIONAL number, not a demographic one: these members are
            // enrolled and cannot yet claim, and reception meets them at the desk not knowing that.
            Series("waiting-period", "In waiting period", "ضمن فترة الانتظار", "count",
                [Point("waiting", "Serving a waiting period", "ضمن فترة انتظار", waiting)],
                ["Population", "Members"]),
        ];
    }

    // ── 2. Utilization ────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AnalyticsSeries>> UtilizationAsync(
        string tenant, AnalyticsFilter f, PermittedPayers p, CancellationToken ct)
    {
        var q = Utilizations(tenant, f, p);

        var byCategory = await q.GroupBy(x => x.BenefitCategoryCode)
            .Select(g => new
            {
                Category = g.Key,
                Limit = g.Sum(x => x.LimitValue),
                Consumed = g.Sum(x => x.ConsumedValue),
            })
            .ToListAsync(ct);

        var byBand = await q.GroupBy(x => x.Band)
            .Select(g => new { Band = g.Key, Members = g.Select(x => x.EnrollmentId).Distinct().Count() })
            .ToListAsync(ct);

        // 19.1b — the split the network view exists to price and this view exists to notice.
        var network = await q.GroupBy(x => x.OutOfNetwork)
            .Select(g => new { OutOfNetwork = g.Key, Consumed = g.Sum(x => x.ConsumedValue), Count = g.Count() })
            .ToListAsync(ct);

        var inNetwork = network.Where(n => !n.OutOfNetwork).Sum(n => n.Consumed);
        var outNetwork = network.Where(n => n.OutOfNetwork).Sum(n => n.Consumed);

        return
        [
            Series("consumed-vs-limit", "Consumed against limit, by benefit category",
                "الاستهلاك مقابل الحد حسب فئة المنفعة", "currency",
                [.. byCategory.Select(c => new AnalyticsPoint(c.Category, c.Category, c.Category, c.Consumed, null, c.Limit))],
                ["Benefit category", "Consumed", "Limit"]),

            // Bands rather than a histogram of percentages: the question is triage, and a band survives the
            // rounding argument a percentage invites (see UtilizationBands).
            Series("band-distribution", "Members by utilization band", "الأعضاء حسب شريحة الاستهلاك", "count",
                [.. byBand.Select(b => Point(b.Band, b.Band, b.Band, b.Members))],
                ["Band", "Members"]),

            Series("threshold-crossings", "Members crossing 80% and 100%", "الأعضاء المتجاوزون ٨٠٪ و١٠٠٪", "count",
            [
                Point("over-80", "At or over 80%", "٨٠٪ أو أكثر",
                    byBand.Where(b => b.Band is nameof(UtilizationBand.High) or nameof(UtilizationBand.Exhausted)).Sum(b => b.Members)),
                Point("over-100", "At or over the limit", "بلغ الحد أو تجاوزه",
                    byBand.Where(b => b.Band == nameof(UtilizationBand.Exhausted)).Sum(b => b.Members)),
            ], ["Threshold", "Members"]),

            Series("network-split", "In-network vs out-of-network", "داخل الشبكة مقابل خارجها", "currency",
            [
                Point("in-network", "In network", "داخل الشبكة", inNetwork),
                Point("out-of-network", "Out of network", "خارج الشبكة", outNetwork),
            ], ["Network", "Consumed"]),
        ];
    }

    // ── 3. Financial ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AnalyticsSeries>> FinancialAsync(
        string tenant, AnalyticsFilter f, PermittedPayers p, CancellationToken ct)
    {
        var q = Costs(tenant, f, p);

        var byPayer = await q.Where(x => x.PayerId != null).GroupBy(x => x.PayerId!.Value)
            .Select(g => new
            {
                Payer = g.Key,
                Claimed = g.Sum(x => x.ClaimedAmount),
                Approved = g.Sum(x => x.ApprovedAmount),
                Adjusted = g.Sum(x => x.AdjustedAmount),
                Net = g.Sum(x => x.NetPayable),
            })
            .ToListAsync(ct);

        var byCategory = await q.GroupBy(x => x.BenefitCategoryCode)
            .Select(g => new { Category = g.Key, Net = g.Sum(x => x.NetPayable) })
            .OrderByDescending(x => x.Net).Take(10)
            .ToListAsync(ct);

        // Cost per active member per month — the number a board asks for and the one most often computed on a
        // denominator nobody wrote down. The denominator here is DISTINCT ACTIVE MEMBERS in the same window.
        var activeMembers = await Enrolments(tenant, f, p)
            .Where(x => x.Status == "Active").Select(x => x.EnrollmentId).Distinct().CountAsync(ct);
        var totalNet = byPayer.Sum(x => x.Net);
        var months = MonthsIn(f);
        var perMemberMonth = activeMembers > 0 && months > 0
            ? Math.Round(totalNet / activeMembers / months, 2, MidpointRounding.AwayFromZero)
            : 0m;

        var payerLabels = await LabelsAsync(tenant, "payer", ct);

        return
        [
            Series("by-payer", "Claimed / approved / adjusted / net by payer",
                "المطالب / المعتمد / المعدّل / الصافي حسب الجهة", "currency",
                [.. byPayer.Select(x => new AnalyticsPoint(x.Payer.ToString(),
                    Label(payerLabels, x.Payer, en: true), Label(payerLabels, x.Payer, en: false), x.Net, x.Payer, x.Claimed))],
                ["Payer", "Net payable", "Claimed"]),

            Series("top-cost-drivers", "Top cost drivers by benefit category",
                "أكبر مسبّبات التكلفة حسب فئة المنفعة", "currency",
                [.. byCategory.Select(x => Point(x.Category, x.Category, x.Category, x.Net))],
                ["Benefit category", "Net payable"]),

            Series("cost-per-member-month", "Cost per active member per month",
                "التكلفة لكل عضو نشط شهريًا", "currency",
                [Point("pmpm", "Per member per month", "لكل عضو شهريًا", perMemberMonth)],
                ["Metric", "Amount"]),
        ];
    }

    // ── 4. Network ────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AnalyticsSeries>> NetworkAsync(
        string tenant, AnalyticsFilter f, PermittedPayers p, CancellationToken ct)
    {
        var q = Costs(tenant, f, p);

        var byTier = await q.GroupBy(x => x.NetworkTierCode ?? "unattributed")
            .Select(g => new { Tier = g.Key, Net = g.Sum(x => x.NetPayable), Claims = g.Sum(x => x.ClaimCount) })
            .ToListAsync(ct);

        var oon = await q.GroupBy(x => x.OutOfNetwork)
            .Select(g => new { g.Key, Net = g.Sum(x => x.NetPayable), Claims = g.Sum(x => x.ClaimCount) })
            .ToListAsync(ct);

        var oonClaims = oon.Where(x => x.Key).Sum(x => x.Claims);
        var allClaims = oon.Sum(x => x.Claims);
        // Leakage as a RATE, not a count: 400 out-of-network claims means nothing until you know whether that
        // is 2% or 40% of activity, and those are different conversations with the Network Team.
        var leakageRate = allClaims > 0
            ? Math.Round((decimal)oonClaims / allClaims * 100m, 1, MidpointRounding.AwayFromZero) : 0m;

        var topProviders = await q.Where(x => x.ProviderId != null).GroupBy(x => x.ProviderId!.Value)
            .Select(g => new { Provider = g.Key, Net = g.Sum(x => x.NetPayable), Claims = g.Sum(x => x.ClaimCount) })
            .OrderByDescending(x => x.Net).Take(10)
            .ToListAsync(ct);

        return
        [
            Series("tier-mix", "Delivered value by network tier", "القيمة المقدَّمة حسب شريحة الشبكة", "currency",
                [.. byTier.Select(t => new AnalyticsPoint(t.Tier, t.Tier, t.Tier, t.Net, null, t.Claims))],
                ["Tier", "Net payable", "Claims"]),

            Series("oon-leakage", "Out-of-network leakage", "التسرّب خارج الشبكة", "percent",
            [
                Point("leakage-rate", "Out-of-network share of claims", "نسبة المطالبات خارج الشبكة", leakageRate),
                Point("leakage-cost", "Out-of-network net payable", "الصافي المستحق خارج الشبكة",
                    oon.Where(x => x.Key).Sum(x => x.Net)),
            ], ["Measure", "Value"]),

            Series("top-providers", "Top providers by value", "أعلى مقدّمي الخدمة قيمةً", "currency",
                [.. topProviders.Select(x => new AnalyticsPoint(x.Provider.ToString(),
                    x.Provider.ToString()[..8], x.Provider.ToString()[..8], x.Net, x.Provider, x.Claims))],
                ["Provider", "Net payable", "Claims"]),
        ];
    }

    // ── 5. Plan comparison ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two or more plans side by side — "is Plan B actually cheaper?".
    ///
    /// <para>Cost per member is the only honest comparison, and it needs BOTH halves from different facts: the
    /// spend from <c>fact_cost</c> and the membership from <c>fact_enrolment</c>. A plan with 12 expensive
    /// members and a plan with 4 000 cheap ones have similar totals and nothing else in common.</para>
    /// </summary>
    public async Task<IReadOnlyList<AnalyticsSeries>> PlanComparisonAsync(
        string tenant, AnalyticsFilter f, PermittedPayers p, IReadOnlyList<Guid> planIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(planIds);
        var labels = await LabelsAsync(tenant, "policy_plan", ct);
        var enrolment = new List<AnalyticsPoint>();
        var costPerMember = new List<AnalyticsPoint>();
        var utilization = new List<AnalyticsPoint>();
        var oonRate = new List<AnalyticsPoint>();

        foreach (var planId in planIds.Distinct())
        {
            var scoped = f with { PolicyPlanId = planId };
            var members = await Enrolments(tenant, scoped, p)
                .Where(x => x.Status == "Active").Select(x => x.EnrollmentId).Distinct().CountAsync(ct);

            var net = await Costs(tenant, scoped, p).SumAsync(x => (decimal?)x.NetPayable, ct) ?? 0m;
            var util = await Utilizations(tenant, scoped, p)
                .GroupBy(_ => 1)
                .Select(g => new { Limit = g.Sum(x => x.LimitValue), Consumed = g.Sum(x => x.ConsumedValue) })
                .FirstOrDefaultAsync(ct);

            var oonClaims = await Costs(tenant, scoped, p).Where(x => x.OutOfNetwork).SumAsync(x => (int?)x.ClaimCount, ct) ?? 0;
            var allClaims = await Costs(tenant, scoped, p).SumAsync(x => (int?)x.ClaimCount, ct) ?? 0;

            var en = Label(labels, planId, en: true);
            var ar = Label(labels, planId, en: false);
            enrolment.Add(new AnalyticsPoint(planId.ToString(), en, ar, members, planId));
            costPerMember.Add(new AnalyticsPoint(planId.ToString(), en, ar,
                members > 0 ? Math.Round(net / members, 2, MidpointRounding.AwayFromZero) : 0m, planId, net));
            utilization.Add(new AnalyticsPoint(planId.ToString(), en, ar,
                util is { Limit: > 0m } ? UtilizationBands.PercentUsed(util.Limit, util.Consumed) ?? 0m : 0m, planId));
            oonRate.Add(new AnalyticsPoint(planId.ToString(), en, ar,
                allClaims > 0 ? Math.Round((decimal)oonClaims / allClaims * 100m, 1, MidpointRounding.AwayFromZero) : 0m, planId));
        }

        return
        [
            Series("plan-enrolment", "Active members by plan", "الأعضاء النشطون حسب الخطة", "count", enrolment,
                ["Plan", "Active members"]),
            Series("plan-cost-per-member", "Net cost per active member", "صافي التكلفة لكل عضو نشط", "currency",
                costPerMember, ["Plan", "Cost per member", "Total net"]),
            Series("plan-utilization", "Utilization of limit", "استهلاك الحد", "percent", utilization,
                ["Plan", "% of limit used"]),
            Series("plan-oon-rate", "Out-of-network rate", "نسبة الخدمات خارج الشبكة", "percent", oonRate,
                ["Plan", "% out of network"]),
        ];
    }

    // ── 6. Outliers & data quality ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The view that finds what the other five average away.
    ///
    /// <para>Members with NO utilization sit here beside members over their limit, and that pairing is the
    /// point: both are outliers, and the second is the one nobody looks for. A member who has consumed nothing
    /// all year is healthy, unaware of their entitlement, or wrongly enrolled — and only the third is a defect,
    /// which is why the row is surfaced for a human rather than counted into a KPI.</para>
    /// </summary>
    public async Task<IReadOnlyList<AnalyticsSeries>> OutliersAsync(
        string tenant, AnalyticsFilter f, PermittedPayers p, CancellationToken ct)
    {
        var util = Utilizations(tenant, f, p);
        var enrol = Enrolments(tenant, f, p);

        var overLimit = await util.Where(x => x.Band == nameof(UtilizationBand.Exhausted))
            .Select(x => x.EnrollmentId).Distinct().CountAsync(ct);
        var nearLimit = await util.Where(x => x.Band == nameof(UtilizationBand.High))
            .Select(x => x.EnrollmentId).Distinct().CountAsync(ct);
        var noUtilization = await util.Where(x => x.Band == nameof(UtilizationBand.Zero))
            .Select(x => x.EnrollmentId).Distinct().CountAsync(ct);

        var missingPlan = await enrol.Where(x => x.PolicyPlanId == null && x.Status == "Active")
            .Select(x => x.EnrollmentId).Distinct().CountAsync(ct);
        var missingGroup = await enrol.Where(x => x.GroupId == null && x.Status == "Active")
            .Select(x => x.EnrollmentId).Distinct().CountAsync(ct);
        var unattributedPayer = await enrol.Where(x => x.PayerId == null)
            .Select(x => x.PolicyId).Distinct().CountAsync(ct);

        return
        [
            Series("limit-outliers", "Members at the edge of their entitlement",
                "أعضاء عند حدود استحقاقهم", "count",
            [
                Point("over-limit", "Over the limit", "تجاوزوا الحد", overLimit),
                Point("near-limit", "80–99% of the limit", "٨٠–٩٩٪ من الحد", nearLimit),
                Point("no-utilization", "No utilization at all", "بدون أي استهلاك", noUtilization),
            ], ["Outlier", "Members"]),

            Series("data-quality", "Data quality findings", "ملاحظات جودة البيانات", "count",
            [
                Point("missing-plan", "Active members with no plan", "أعضاء نشطون بلا خطة", missingPlan),
                Point("missing-group", "Active members with no group", "أعضاء نشطون بلا مجموعة", missingGroup),
                // The pre-19.2 rows the 19.7 backfill retires. Counted here so the backfill has a number to
                // drive to zero rather than being declared done.
                Point("unattributed-payer", "Policies with no payer", "وثائق بلا جهة ممولة", unattributedPayer),
            ], ["Finding", "Count"]),
        ];
    }

    /// <summary>The drill-down list behind an outlier segment: enrolment ids and their standing, nothing more.
    /// The identity step is a separate, audited call — a list that already carried names would have made the
    /// audit event a formality.</summary>
    public async Task<IReadOnlyList<OutlierRow>> OutlierMembersAsync(
        string tenant, AnalyticsFilter f, PermittedPayers p, string band, int limit, CancellationToken ct)
    {
        var rows = await Utilizations(tenant, f, p)
            .Where(x => x.Band == band)
            .GroupBy(x => new { x.EnrollmentId, x.BeneficiaryId, x.PolicyId, x.PolicyPlanId })
            .Select(g => new OutlierRow(
                g.Key.EnrollmentId, g.Key.BeneficiaryId, g.Key.PolicyId, g.Key.PolicyPlanId,
                g.Sum(x => x.LimitValue), g.Sum(x => x.ConsumedValue), band))
            .OrderByDescending(r => r.Consumed)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);
        return rows;
    }

    // ── Compare mode ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Turn two runs of the same series into delta chips.</summary>
    /// <remarks><paramref name="higherIsBetter"/> is supplied per series because direction and desirability are
    /// different facts: enrolment up is good news and cost per member up is not, and a chip that colours both
    /// green has said nothing.</remarks>
    public static IReadOnlyList<AnalyticsDelta> Deltas(
        IReadOnlyList<AnalyticsSeries> current, IReadOnlyList<AnalyticsSeries> previous,
        Func<string, bool?> higherIsBetter)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(higherIsBetter);

        var deltas = new List<AnalyticsDelta>();
        foreach (var series in current)
        {
            var before = previous.FirstOrDefault(s => s.Key == series.Key);
            foreach (var point in series.Points)
            {
                var was = before?.Points.FirstOrDefault(x => x.Key == point.Key)?.Value ?? 0m;
                var change = was == 0m ? (decimal?)null
                    : Math.Round((point.Value - was) / Math.Abs(was) * 100m, 1, MidpointRounding.AwayFromZero);
                var direction = point.Value > was ? "Up" : point.Value < was ? "Down" : "Flat";
                var better = higherIsBetter($"{series.Key}.{point.Key}") is not { } good || direction == "Flat"
                    ? (bool?)null
                    : good == (direction == "Up");
                deltas.Add(new AnalyticsDelta($"{series.Key}.{point.Key}", point.LabelEn, point.LabelAr,
                    point.Value, was, change, direction, better));
            }
        }
        return deltas;
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<Guid, DimensionLabel>> LabelsAsync(string tenant, string kind, CancellationToken ct) =>
        await db.DimensionLabels.AsNoTracking()
            .Where(d => d.TenantId == tenant && d.Kind == kind)
            .ToDictionaryAsync(d => d.DimensionId, d => d, ct);

    /// <summary>The label, or a short id. Never an invented name: an id the dashboard cannot name is a gap in
    /// the dimension feed, and printing "Unknown plan" hides it while a truncated id sends someone looking.</summary>
    private static string Label(Dictionary<Guid, DimensionLabel> labels, Guid id, bool en) =>
        labels.TryGetValue(id, out var l) ? (en ? l.LabelEn : l.LabelAr) : id.ToString()[..8];

    private static AnalyticsPoint Point(string key, string en, string ar, decimal value, Guid? id = null) =>
        new(key, en, ar, value, id);

    private static AnalyticsSeries Series(
        string key, string titleEn, string titleAr, string unit,
        IReadOnlyList<AnalyticsPoint> points, IReadOnlyList<string> columns) =>
        new(key, titleEn, titleAr, unit, points, SummarizeEn(titleEn, points), SummarizeAr(titleAr, points), columns);

    /// <summary>The one-line text summary that accompanies every chart (U6). Written server-side so it always
    /// describes the data actually plotted rather than a caption someone forgot to update.</summary>
    private static string SummarizeEn(string title, IReadOnlyList<AnalyticsPoint> points)
    {
        if (points.Count == 0) return $"{title}: no data for the selected filters.";
        var top = points.MaxBy(p => p.Value)!;
        var total = points.Sum(p => p.Value);
        return string.Create(CultureInfo.InvariantCulture,
            $"{title}: {points.Count} series totalling {total:0.##}; highest is {top.LabelEn} at {top.Value:0.##}.");
    }

    private static string SummarizeAr(string title, IReadOnlyList<AnalyticsPoint> points)
    {
        if (points.Count == 0) return $"{title}: لا توجد بيانات ضمن عوامل التصفية المحددة.";
        var top = points.MaxBy(p => p.Value)!;
        var total = points.Sum(p => p.Value);
        return string.Create(CultureInfo.InvariantCulture,
            $"{title}: {points.Count} سلاسل بإجمالي {total:0.##}؛ الأعلى {top.LabelAr} بقيمة {top.Value:0.##}.");
    }

    /// <summary>Whole months in the filter window, at least one — the denominator of "per member per month".
    /// A zero denominator would render an infinity, and a silently-1 denominator would report a month's cost
    /// as a year's.</summary>
    private static decimal MonthsIn(AnalyticsFilter f)
    {
        if (f.From is not { } from || f.To is not { } to) return 1m;
        var days = Math.Max(1, to.DayNumber - from.DayNumber + 1);
        return Math.Max(1m, Math.Round(days / 30.44m, 2, MidpointRounding.AwayFromZero));
    }
}

/// <summary>A drill-down row: pointers and figures, never identity. Resolving the beneficiary is the audited
/// step that comes next.</summary>
public sealed record OutlierRow(
    Guid EnrollmentId, Guid BeneficiaryId, Guid PolicyId, Guid? PolicyPlanId,
    decimal Limit, decimal Consumed, string Band);
