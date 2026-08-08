using Mersal.BenefitPricing;

namespace Mersal.Reporting.Domain;

/// <summary>
/// Phase 19.6b — the dashboard's shared filter bar, in the SAME vocabulary as 19.5's policy/member query and
/// 19.5b's extracts.
///
/// <para>One record for all six views rather than a filter per view. The filter bar is shared in the UI, the
/// URL is shareable, and a recipient opening someone else's link on a different tab must see the same
/// narrowing applied. Per-view filter types would have made "carry the active filters through a drill-down"
/// a translation step, and a translation step is where a payer filter quietly stops being applied.</para>
///
/// <para><b>Payer scope is not in here on purpose.</b> A caller's permitted payers are resolved server-side per
/// request (<c>PermittedPayers</c>) and intersected with <see cref="PayerId"/>; if scope were a filter field
/// it would be a value the client sends, and a client-sent scope is not a scope.</para>
/// </summary>
public sealed record AnalyticsFilter(
    Guid? PayerId = null,
    Guid? PolicyId = null,
    Guid? PolicyPlanId = null,
    Guid? GroupId = null,
    Guid? BranchId = null,
    string? NetworkTierCode = null,
    string? BenefitCategoryCode = null,
    string? MemberStatus = null,
    string? Relationship = null,
    UtilizationBand? Band = null,
    DateOnly? From = null,
    DateOnly? To = null,
    /// <summary>Point-in-time view. Distinct from <see cref="To"/>: a range ending 31 March asks "what
    /// happened up to March"; an as-of date asks "what did the book look like on that day". The enrolment
    /// view needs the second, and conflating them gives a number that is wrong for both.</summary>
    DateOnly? AsOf = null)
{
    /// <summary>The comparison window for compare mode — the same length, immediately before.</summary>
    /// <remarks>Period-over-period rather than a fixed "last month": comparing a 7-day range against a
    /// calendar month is the kind of chart that gets screenshotted into a board pack.</remarks>
    public AnalyticsFilter PreviousPeriod()
    {
        if (From is not { } f || To is not { } t) return this;
        var days = t.DayNumber - f.DayNumber + 1;
        return this with { From = f.AddDays(-days), To = f.AddDays(-1), AsOf = null };
    }

    /// <summary>Does this filter narrow to a single payer the caller has actually been granted?</summary>
    public bool HasPayerNarrowing => PayerId is not null;
}

/// <summary>The six questions the dashboard answers. An enum rather than free strings so an unknown view is a
/// 400 at the edge, not an empty chart that reads as "no data".</summary>
public enum AnalyticsView
{
    Enrolment,
    Utilization,
    Financial,
    Network,
    PlanComparison,
    Outliers,
}

/// <summary>
/// One plotted series: a label, a value, and the id the label came from.
///
/// <para><see cref="DimensionId"/> is what makes drill-down work without the client re-resolving a name back
/// to an id — a round trip that guesses, and guesses wrong the moment two plans share a label.</para>
/// </summary>
public sealed record AnalyticsPoint(
    string Key, string LabelEn, string LabelAr, decimal Value, Guid? DimensionId = null, decimal? Secondary = null);

/// <summary>
/// A chart plus the accessible table that always accompanies it.
///
/// <para>The R2 audit finding U6 is that a data table hidden behind a default-off toggle is not an
/// alternative — it is a feature nobody finds. So the SERVER returns the rows, the client renders them
/// unconditionally in the DOM, and there is no toggle to leave switched off. <see cref="SummaryEn"/> is the
/// one-line text summary a screen-reader user hears before deciding whether to read the table at all.</para>
/// </summary>
public sealed record AnalyticsSeries(
    string Key,
    string TitleEn,
    string TitleAr,
    string Unit,                                    // count / currency / percent
    IReadOnlyList<AnalyticsPoint> Points,
    string SummaryEn,
    string SummaryAr,
    /// <summary>
    /// Column headers for the accessible table, in the order the points render.
    ///
    /// <para>Bilingual, and <see cref="BiText"/> rather than a second parallel list, for the reason the R1
    /// audit (§3.1) recorded: this was one monolingual array, so every accessible chart alternative rendered
    /// MOVEMENT / MEMBERS in English inside an otherwise Arabic page — the only untranslated text left on the
    /// screen, sitting on the element that exists FOR the reader who cannot see the chart. Title, label and
    /// summary were all already authored in both languages; the headers were the gap.</para>
    ///
    /// <para>A translation table on the client was the alternative and is worse: two places would then decide
    /// what "Net payable" is called, and they drift. Two parallel arrays would be worse again — nothing stops
    /// them differing in LENGTH, which renders a table with more headers than columns.</para>
    /// </summary>
    IReadOnlyList<BiText> Columns);

/// <summary>A view's payload: its series, plus the delta chips when compare mode is on.</summary>
public sealed record AnalyticsViewResult(
    string View,
    IReadOnlyList<AnalyticsSeries> Series,
    IReadOnlyList<AnalyticsDelta> Deltas,
    /// <summary>True when the caller's payer scope narrowed the aggregate. Surfaced so a small number reads as
    /// "your scope" rather than "the programme shrank".</summary>
    bool PayerScopeApplied,
    /// <summary>Figures that could not be composed (a source read model was empty or unavailable). NAMED, for
    /// the 19.5b reason: a total silently missing a component is not narrower, it is wrong.</summary>
    IReadOnlyList<string> Unavailable);

/// <summary>
/// A period-over-period movement.
///
/// <para><see cref="Direction"/> is Up/Down/Flat as a WORD, not a colour or an arrow glyph: the four-cue rule
/// (hue + icon + shape + text) means the text cue has to exist in the payload, or the client can only invent
/// it. <see cref="Better"/> is separate because direction and desirability are not the same — enrolment up is
/// good, cost per member up is not, and only the server knows which series is which.</para>
/// </summary>
public sealed record AnalyticsDelta(
    string Key,
    string LabelEn,
    string LabelAr,
    decimal Current,
    decimal Previous,
    decimal? PercentChange,
    string Direction,                               // Up / Down / Flat
    bool? Better);
