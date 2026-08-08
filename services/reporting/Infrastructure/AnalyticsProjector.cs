using System.Globalization;
using Mersal.BenefitPricing;
using Mersal.Reporting.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Reporting.Infrastructure;

/// <summary>
/// Phase 19.6b — projects policy, benefit and claims events into the analytical read model.
///
/// <para>Kept separate from <see cref="EventProjector"/> (phase 8.2) rather than folded into its switch. The
/// two answer different questions from different sources, and the older projector's facts are strictly
/// de-identified counts while these carry a beneficiary POINTER for audited drill-down. One 300-line switch
/// over both would make it easy to add a clinical field to the wrong table.</para>
///
/// <para>Idempotent on the same ledger: an event id already in <c>processed_event</c> is a no-op, so a redelivery
/// cannot double-count a member into the enrolment curve.</para>
/// </summary>
public sealed class AnalyticsProjector(ReportingDbContext db, TimeProvider clock)
{
    /// <summary>True when the event produced a fact; false when it is not an analytics event. Deduplication and
    /// the SaveChanges belong to <see cref="EventProjector"/>, which owns the ledger and the transaction — this
    /// only stages rows, so it is synchronous despite the Task-returning shape its caller awaits.</summary>
    public Task<bool> ProjectAsync(ReportingEvent ev, DateOnly period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return Task.FromResult(Project(ev, period));
    }

    private bool Project(ReportingEvent ev, DateOnly period)
    {

        switch (ev.EventType)
        {
            case "MemberEnrolled":
                AddEnrolment(ev, period, "Enrolled");
                return true;
            case "MemberTerminated":
                AddEnrolment(ev, period, "Terminated");
                return true;
            case "MemberReinstated":
                AddEnrolment(ev, period, "Reinstated");
                return true;
            case "MemberPlanChanged":
                AddEnrolment(ev, period, "PlanChanged");
                return true;
            case "MemberGroupChanged":
                AddEnrolment(ev, period, "GroupChanged");
                return true;
            case "MemberEnrolmentCancelled":
                AddEnrolment(ev, period, "Cancelled");
                return true;

            /*
             * The accumulator moved. Phase 18 owns consumed_value and remains its only writer; this observes
             * the movement and stores the resulting standing so the dashboard never reads the accumulator.
             *
             * `BenefitConsumed` used to share this case and is gone. No service publishes it, and none should:
             * policy-service already emits `CoverageLimitChanged` for exactly this moment, from
             * `BenefitConsumptionApplier` — the one writer of the accumulator. Two names for one movement is
             * how a fact gets counted twice the day somebody wires the second one.
             */
            case "CoverageLimitChanged":
                AddUtilization(ev, period);
                return true;

            /*
             * `ClaimSettled` is the terminal claim decision (claims publishes it as `Claim{Status}.v1`).
             *
             * `ClaimAdjudicated` used to share this case and is gone — it was the wrong GRAIN, not merely
             * unwired. Adjudication is a pre-decision recommendation: booking it as cost would record money a
             * reviewer may still reduce, and record it again when they do. A cost fact belongs to the moment
             * the money is final.
             */
            case "ClaimSettled":
                AddCost(ev, period);
                return true;

            case "DimensionLabelled":
                UpsertLabel(ev);
                return true;

            default:
                return false;
        }
    }

    private void AddEnrolment(ReportingEvent ev, DateOnly period, string movement) =>
        db.EnrolmentFacts.Add(new EnrolmentFact
        {
            EventId = ev.EventId,
            TenantId = ev.TenantId,
            PayerId = GuidOrNull(ev, "payerId"),
            PolicyId = Guid(ev, "policyId"),
            PolicyPlanId = GuidOrNull(ev, "policyPlanId"),
            GroupId = GuidOrNull(ev, "groupId"),
            BranchId = GuidOrNull(ev, "branchId"),
            Relationship = Field(ev, "relationship", "Principal"),
            // The movement is authoritative for Terminated/Cancelled even when the payload's status lags: a
            // termination event whose row still reads Active is a race, and the churn curve must not lose it.
            Status = movement switch
            {
                "Terminated" => "Terminated",
                "Cancelled" => "Cancelled",
                _ => Field(ev, "status", "Active"),
            },
            BeneficiaryId = Guid(ev, "beneficiaryId"),
            EnrollmentId = Guid(ev, "enrollmentId"),
            Movement = movement,
            // Only meaningful at enrolment: a member reinstated years later is not "waiting" again.
            InWaitingPeriod = movement == "Enrolled" && WaitingPeriodOpen(ev, period),
            Period = period,
            OccurredAt = ev.OccurredAt,
        });

    private void AddUtilization(ReportingEvent ev, DateOnly period)
    {
        var limit = Money(ev, "limitValue");
        var consumed = Money(ev, "consumedValue");
        var hasCoverage = Bool(ev, "hasCoverage", fallback: true);
        // The SAME classifier policy query uses (libs/benefit-pricing). A local copy of the thresholds is how a
        // member ends up High on the dashboard and Medium in a query with both screens looking correct.
        var band = UtilizationBands.Of(limit, consumed, hasCoverage);

        db.MemberUtilizationFacts.Add(new MemberUtilizationFact
        {
            EventId = ev.EventId,
            TenantId = ev.TenantId,
            PayerId = GuidOrNull(ev, "payerId"),
            PolicyId = Guid(ev, "policyId"),
            PolicyPlanId = GuidOrNull(ev, "policyPlanId"),
            GroupId = GuidOrNull(ev, "groupId"),
            BranchId = GuidOrNull(ev, "branchId"),
            BeneficiaryId = Guid(ev, "beneficiaryId"),
            EnrollmentId = Guid(ev, "enrollmentId"),
            BenefitCategoryCode = Field(ev, "benefitCategoryCode", "unknown"),
            NetworkTierCode = FieldOrNull(ev, "networkTierCode"),
            OutOfNetwork = Bool(ev, "outOfNetwork"),
            LimitValue = limit,
            ConsumedValue = consumed,
            Remaining = limit <= 0m ? null : Math.Max(0m, limit - consumed),
            Band = band.ToString(),
            Period = period,
            OccurredAt = ev.OccurredAt,
        });
    }

    private void AddCost(ReportingEvent ev, DateOnly period)
    {
        var approved = Money(ev, "approvedAmount");
        var adjusted = Money(ev, "adjustedAmount");
        db.CostFacts.Add(new CostFact
        {
            EventId = ev.EventId,
            TenantId = ev.TenantId,
            PayerId = GuidOrNull(ev, "payerId"),
            PolicyId = GuidOrNull(ev, "policyId"),
            PolicyPlanId = GuidOrNull(ev, "policyPlanId"),
            NetworkTierCode = FieldOrNull(ev, "networkTierCode"),
            OutOfNetwork = Bool(ev, "outOfNetwork"),
            BenefitCategoryCode = Field(ev, "benefitCategoryCode", "unknown"),
            ProviderId = GuidOrNull(ev, "providerId"),
            ClaimedAmount = Money(ev, "claimedAmount"),
            ApprovedAmount = approved,
            AdjustedAmount = adjusted,
            // Stored rather than computed on read, so the dashboard's "net" and the settlement advice's "net"
            // cannot drift into meaning two different things.
            NetPayable = approved - adjusted,
            CurrencyCode = Field(ev, "currencyCode", "EGP"),
            ClaimCount = int.TryParse(Field(ev, "claimCount", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1,
            Period = period,
            OccurredAt = ev.OccurredAt,
        });
    }

    private void UpsertLabel(ReportingEvent ev)
    {
        var id = Guid(ev, "dimensionId");
        var kind = Field(ev, "kind", "payer");
        var row = db.DimensionLabels.Local.FirstOrDefault(d => d.DimensionId == id && d.Kind == kind)
                  ?? db.DimensionLabels.Find(id, kind);
        if (row is null)
        {
            db.DimensionLabels.Add(new DimensionLabel
            {
                DimensionId = id, Kind = kind, TenantId = ev.TenantId,
                LabelEn = Field(ev, "labelEn", "—"), LabelAr = Field(ev, "labelAr", "—"),
                Code = FieldOrNull(ev, "code"), UpdatedAt = clock.GetUtcNow(),
            });
            return;
        }
        row.LabelEn = Field(ev, "labelEn", row.LabelEn);
        row.LabelAr = Field(ev, "labelAr", row.LabelAr);
        row.Code = FieldOrNull(ev, "code") ?? row.Code;
        row.UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>Is the member still inside their waiting period on the day the fact is written?</summary>
    private static bool WaitingPeriodOpen(ReportingEvent ev, DateOnly period) =>
        DateOnly.TryParse(Field(ev, "waitingPeriodEndsOn"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ends)
        && ends > period;

    private static string Field(ReportingEvent ev, string key, string fallback = "") =>
        ev.Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static string? FieldOrNull(ReportingEvent ev, string key) =>
        ev.Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static Guid Guid(ReportingEvent ev, string key) =>
        System.Guid.TryParse(Field(ev, key), out var g) ? g : System.Guid.Empty;

    private static Guid? GuidOrNull(ReportingEvent ev, string key) =>
        System.Guid.TryParse(Field(ev, key), out var g) ? g : null;

    private static decimal Money(ReportingEvent ev, string key) =>
        decimal.TryParse(Field(ev, key), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static bool Bool(ReportingEvent ev, string key, bool fallback = false) =>
        ev.Fields.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;
}
