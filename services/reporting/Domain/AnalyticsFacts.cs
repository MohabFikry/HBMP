namespace Mersal.Reporting.Domain;

// ── Phase 19.6b — the analytical read model over policy & member administration.
//
// ============================================================================================================
// WHY THESE ARE FACTS AND NOT QUERIES
// ============================================================================================================
// 19.6b's instruction is explicit: "EXTEND the reporting read-model pattern; do not query PHI tables live from
// the dashboard." A dashboard that joined policy.enrollment and policy.coverage_limit would be six aggregate
// scans over the transactional benefit spine — the same tables a reception desk is checking eligibility
// against — and it would put row-level PHI one `SELECT *` away from a screen whose whole purpose is to show
// totals. So the dashboard reads only from here.
//
// ============================================================================================================
// WHAT IS DELIBERATELY ABSENT
// ============================================================================================================
// No beneficiary NAME, no identifier, no diagnosis, no clinical text. `BeneficiaryId` appears on
// EnrolmentFact and MemberUtilizationFact because the outlier views must be able to say "these 14 members are
// over 80% of their limit" and then hand a permission-gated, AUDITED drill-down the id to resolve — a list of
// anonymous rows is not actionable, and re-deriving identity from a join in the dashboard is exactly what this
// model exists to prevent. The id is a pointer, never a projection of the person.
//
// FinancialFact (phase 8.2) already carries the invariant that finance sees no diagnosis. CostFact keeps it:
// there is no clinical column here, and a test asserts it.

/// <summary>
/// Daily membership snapshot by the dimensions the dashboard filters on (payer / policy / plan / group /
/// branch / relationship / status).
///
/// <para>A SNAPSHOT rather than a running total, because both questions get asked: "how many members do we have
/// today" is answered by the latest row, and "how did that move" needs yesterday's alongside it. A stored count
/// that only ever incremented could answer the first and never the second.</para>
/// </summary>
public sealed class EnrolmentFact
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    /// <summary>Deduplication key — the domain event that produced this row. Unique.</summary>
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = default!;

    public Guid? PayerId { get; set; }
    public Guid PolicyId { get; set; }
    public Guid? PolicyPlanId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? BranchId { get; set; }
    public string Relationship { get; set; } = default!;   // Principal / Spouse / Child / …
    public string Status { get; set; } = default!;         // Active / Terminated / Suspended / Cancelled

    /// <summary>Pointer for an audited drill-down, never a projection of the person. See the header.</summary>
    public Guid BeneficiaryId { get; set; }
    public Guid EnrollmentId { get; set; }

    /// <summary>What happened: Enrolled / Terminated / Reinstated / PlanChanged / Cancelled. Churn is
    /// `new` vs `terminated` over a period, which needs the movement, not just the standing.</summary>
    public string Movement { get; set; } = default!;

    /// <summary>True while the member is inside their waiting period on <see cref="Period"/> — the
    /// "waiting-period population" the enrolment view reports.</summary>
    public bool InWaitingPeriod { get; set; }

    public DateOnly Period { get; set; }                   // Africa/Cairo day of the movement
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Consumed-versus-limit per member per benefit category, refreshed from the accumulator's own events.
///
/// <para>Carries the member grain, not just the category total, because three of the six views need it: the
/// "% of limit" distribution, the 80/100% threshold crossings, and the outlier list. Aggregating at write time
/// to category level would answer the FINANCIAL view and silently make the other three impossible.</para>
/// </summary>
public sealed class MemberUtilizationFact
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = default!;

    public Guid? PayerId { get; set; }
    public Guid PolicyId { get; set; }
    public Guid? PolicyPlanId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid BeneficiaryId { get; set; }
    public Guid EnrollmentId { get; set; }

    public string BenefitCategoryCode { get; set; } = default!;
    /// <summary>The network tier the service was delivered at (19.1b), or null when it was not tier-attributed.
    /// The in-network / out-of-network split of the utilization view is built from this.</summary>
    public string? NetworkTierCode { get; set; }
    /// <summary>True when the tier is flagged out-of-network. Stored rather than re-derived: tier membership is
    /// effective-dated and a provider that moves tiers must not retroactively reclassify past activity.</summary>
    public bool OutOfNetwork { get; set; }

    public decimal LimitValue { get; set; }
    public decimal ConsumedValue { get; set; }
    /// <summary>Null when the category is unbounded — see <c>UtilizationBand.Unlimited</c>; zero would read as
    /// "nothing left" on something that was never metered.</summary>
    public decimal? Remaining { get; set; }
    /// <summary>The band, computed with <c>Mersal.BenefitPricing.UtilizationBands</c> — the same code policy
    /// query uses, so a member cannot be High here and Medium there.</summary>
    public string Band { get; set; } = default!;

    public DateOnly Period { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Claimed / approved / adjusted / net by payer, plan, tier and category — the financial view's grain.
///
/// <para><b>There is deliberately no clinical column.</b> Not "we did not add one yet": the FINANCIAL view is
/// specified as "No diagnoses anywhere", the finance role holds only the financial reporting zone, and a
/// diagnosis column here would put one behind an authorization check that was never designed to guard it. A
/// test asserts the absence.</para>
/// </summary>
public sealed class CostFact
{
    public Guid FactId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = default!;

    public Guid? PayerId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? PolicyPlanId { get; set; }
    public string? NetworkTierCode { get; set; }
    public bool OutOfNetwork { get; set; }
    public string BenefitCategoryCode { get; set; } = default!;
    public Guid? ProviderId { get; set; }

    public decimal ClaimedAmount { get; set; }
    public decimal ApprovedAmount { get; set; }
    public decimal AdjustedAmount { get; set; }
    /// <summary>Approved − adjusted. Stored rather than computed on read so the dashboard's arithmetic and the
    /// settlement advice cannot disagree about what "net" means.</summary>
    public decimal NetPayable { get; set; }
    public string CurrencyCode { get; set; } = "EGP";
    public int ClaimCount { get; set; } = 1;

    public DateOnly Period { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Label lookup for a dimension id.
///
/// <para>One table rather than dim_payer / dim_plan / dim_group / dim_branch, because they are all the same
/// shape — id, kind, bilingual label — and four near-identical tables would each need their own upsert, index
/// and RLS policy for no gain. The dashboard needs a NAME for an id; that is the whole requirement.</para>
///
/// <para>Labels are denormalised on purpose: the payer a policy belonged to when a fact was written is the
/// payer that fact is about, and renaming the payer must not silently restate last year's report.</para>
/// </summary>
public sealed class DimensionLabel
{
    public Guid DimensionId { get; set; }
    public string Kind { get; set; } = default!;           // payer / policy / policy_plan / group / branch / category / tier
    public string TenantId { get; set; } = default!;
    public string LabelEn { get; set; } = default!;
    public string LabelAr { get; set; } = default!;
    /// <summary>Short code where the dimension has one (payer code, plan label, category code, tier code).</summary>
    public string? Code { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
