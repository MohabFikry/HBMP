namespace Mersal.Reporting.Domain;

// Report result contracts (aggregate, PHI-free). Shared by the query service, the API and the dashboard contracts.

public sealed record TatRow(string Dimension, long Count, double AvgTatSeconds, double P95TatSeconds, long SlaBreaches);
public sealed record ApprovalTatReport(long Total, double AvgTatSeconds, double P95TatSeconds, long SlaBreaches, IReadOnlyList<TatRow> ByPriority);

public sealed record PendingRow(string Status, string Priority, string AgeBucket, long Count, long SlaBreaches);
public sealed record PendingApprovalsReport(long Total, long SlaBreaches, IReadOnlyList<PendingRow> Rows);

/*
 * `ClinicNameEn` / `ClinicNameAr` accompany the id; they do not replace it.
 *
 * A supervisor reconciling a figure against another system needs the key, and a label that has silently
 * stood in for one is a label nobody can check. An unlabelled clinic — a location that predates the
 * dimension feed, or a walk-in encounter with no appointment to read a location from — keeps its id and says
 * so, rather than borrowing a neighbour's name or reporting as "unknown".
 */
public sealed record WorkloadRow(string ClinicId, string? ClinicNameEn, string? ClinicNameAr, DateOnly Period, long Encounters);
public sealed record ClinicWorkloadReport(IReadOnlyList<WorkloadRow> Rows);

public sealed record UtilizationRow(string Code, long Count);
public sealed record UtilizationReport(string Dimension, IReadOnlyList<UtilizationRow> Rows);

public sealed record NoShowRow(string ClinicId, string? ClinicNameEn, string? ClinicNameAr, long Booked, long Attended, long NoShow, double NoShowRate);
public sealed record NoShowReport(long Booked, long Attended, long NoShow, double NoShowRate, IReadOnlyList<NoShowRow> ByClinic);

/*
 * The de-identified drill-down behind an SLA-breach COUNT.
 *
 * A supervisor told "twelve breaches" and given no way to see which twelve is being asked to trust a number
 * they cannot audit. What this carries is the authorization NUMBER, its priority, how long it has waited and
 * who holds it — enough to act on, and deliberately not a beneficiary: the Medical Director holds `auth:read`
 * and could open the case, but a supervisor who opens individual files to check them is doing the reviewer's
 * job, and a portal that made it one click would be inviting exactly that.
 */
public sealed record SlaBreachRow(string AuthNo, string Priority, string Status, string AgeBucket, long AgeSeconds, string? ReviewerId);
public sealed record SlaBreachReport(long Total, IReadOnlyList<SlaBreachRow> Rows);

/// <summary>Claim outcomes and what they cost, for the oversight portal's Claims &amp; Cost view. Financial
/// zone: amounts and coded outcomes, never a diagnosis and never a claimant.</summary>
public sealed record ClaimOutcomeRow(string Outcome, long Count);
public sealed record ClaimsSummaryReport(
    long Decided,
    decimal TotalAllowed,
    IReadOnlyList<ClaimOutcomeRow> ByOutcome,
    IReadOnlyList<FinancialRow> ByServiceLine,
    IReadOnlyList<RejectionReasonRow> TopDenialReasons);

public sealed record CodeRankRow(string Code, long Count);
public sealed record TopCodesReport(string Kind, IReadOnlyList<CodeRankRow> Rows);

public sealed record RejectionReasonRow(string ReasonCode, long Count);
public sealed record RejectedRequestsReport(long Total, IReadOnlyList<RejectionReasonRow> ByReason);

public sealed record FinancialRow(string ServiceLine, decimal Amount, long Count);
public sealed record FinancialSummaryReport(decimal TotalAmount, long TotalCount, IReadOnlyList<FinancialRow> ByServiceLine);

/// <summary>Age-bucketing for pending approvals (data-driven, PHI-free).</summary>
public static class AgeBuckets
{
    public static string Of(TimeSpan age) => age switch
    {
        { TotalHours: < 4 } => "<4h",
        { TotalHours: < 24 } => "4-24h",
        { TotalDays: < 3 } => "1-3d",
        _ => ">3d",
    };
}

/// <summary>p95 over a sample using nearest-rank; pure so it is unit-tested and reused by the query service.</summary>
public static class Percentile
{
    public static double P95(IReadOnlyList<long> values) => Of(values, 0.95);

    public static double Of(IReadOnlyList<long> values, double q)
    {
        if (values is null || values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var rank = (int)Math.Ceiling(q * sorted.Length);
        var idx = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }
}
