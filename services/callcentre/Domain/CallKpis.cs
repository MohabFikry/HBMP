namespace Mersal.CallCentre.Domain;

// Contact-centre KPIs (phase 15.6). AGGREGATE-ONLY — no PHI, no clinical field, no member identity. These are the
// numbers a supervisor needs (calls handled, average handle time, first-contact resolution, reason mix, appointment
// actions, verification-failure + abandoned rates), exposed for the dashboard contracts. Every figure is a count or
// a ratio over the call log — nothing here identifies a member.

/// <summary>A PHI-free call-centre KPI snapshot for a window.</summary>
public sealed record CallKpis(
    DateTimeOffset From,
    DateTimeOffset To,
    int CallsHandled,
    double AvgHandleSeconds,
    double FirstContactResolutionRate,
    double VerificationFailureRate,
    double AbandonedRate,
    int AppointmentsBooked,
    int AppointmentsRescheduled,
    int AppointmentsCancelled,
    IReadOnlyDictionary<string, int> ReasonMix);

/// <summary>Pure ratio helpers so the aggregation and its tests share one definition (avoids divide-by-zero).</summary>
public static class KpiMath
{
    public static double Ratio(int numerator, int denominator) => denominator == 0 ? 0d : (double)numerator / denominator;
}
