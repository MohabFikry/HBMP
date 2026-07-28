using System.Globalization;
using Mersal.BenefitPricing;

namespace Mersal.Policy.Domain;

// Phase 19.5 — the shared filter/sort/paging vocabulary for policy query and member query (design 38 §4.4).
//
// ============================================================================================================
// ONE VOCABULARY, DEFINED ONCE
// ============================================================================================================
// 19.5b's extract engine and 19.6b's dashboard are both specified as reusing "the same filter vocabulary as
// §4.4". If each of the three defined its own utilization bands, a member could sit in High on the dashboard,
// Medium in an extract and neither in a query, and every one of those screens would look correct on its own.
// So the bands, the sort allow-lists and the paging clamps live here, in the domain, and the read paths depend
// on them rather than restating them.

// 19.6b — UtilizationBand / UtilizationBands MOVED to libs/benefit-pricing so reporting-service can classify
// members with the same code this query does. The vocabulary comment above demanded one definition; keeping it
// in a single service's domain guaranteed a second one the moment a different service needed it. Re-exported
// here so every existing `Mersal.Policy.Domain.UtilizationBand` reference still resolves.

/// <summary>Where a member stands against their waiting period on a given date.</summary>
public enum WaitingPeriodState
{
    /// <summary>No waiting period was applied at enrolment.</summary>
    None,
    /// <summary>Still inside it — enrolled, but services in the affected categories are not yet payable. The
    /// state a receptionist most needs to see BEFORE the member is sent to a clinic.</summary>
    Serving,
    /// <summary>Served out; the member is fully in benefit.</summary>
    Served,
}

/// <summary>A member-count band for policy query — "policies with 100–499 members".</summary>
public enum MemberCountBand { Empty, Small, Medium, Large, VeryLarge }

public static class MemberCountBands
{
    /// <summary>Empty = 0, Small &lt; 50, Medium &lt; 250, Large &lt; 1000, VeryLarge ≥ 1000. Empty is its own
    /// band for the same reason Zero is: a policy that was set up and never enrolled anyone is a data-quality
    /// finding, not a small policy.</summary>
    public static MemberCountBand Of(int memberCount) => memberCount switch
    {
        <= 0 => MemberCountBand.Empty,
        < 50 => MemberCountBand.Small,
        < 250 => MemberCountBand.Medium,
        < 1000 => MemberCountBand.Large,
        _ => MemberCountBand.VeryLarge,
    };

    public static bool TryParse(string? raw, out MemberCountBand band) =>
        Enum.TryParse(raw, ignoreCase: true, out band);
}

/// <summary>
/// Page + page size, clamped.
///
/// <para>The cap is not a performance detail — it is a disclosure control. An uncapped page size turns a
/// paginated, audited query into a bulk export that nobody classified as one, and the audit event would record
/// a single innocuous read. Bulk extraction has its own gated, filter-snapshotted path (19.5b); this is not
/// it.</para>
/// </summary>
public sealed record PageRequest(int Page, int PageSize)
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;

    public static PageRequest Of(int? page, int? pageSize) =>
        new(Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    public int Skip => (Page - 1) * PageSize;
}

/// <summary>A validated sort instruction. Parsed against an explicit allow-list — a sort field is a column name
/// reaching the database from a query string, and the allow-list is what keeps it from being anything else.</summary>
public sealed record SortRequest(string Field, bool Descending)
{
    /// <summary>Parse <c>field</c> or <c>-field</c> (leading minus = descending) against
    /// <paramref name="allowed"/>. An unknown field is REJECTED rather than silently defaulted: a caller who
    /// sorted by "cost" and got member-number order would read the first page as the answer to a question they
    /// did not ask.</summary>
    public static bool TryParse(string? raw, IReadOnlySet<string> allowed, string fallback, out SortRequest sort)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (string.IsNullOrWhiteSpace(raw)) { sort = new SortRequest(fallback, false); return true; }

        var desc = raw.StartsWith('-');
        var field = (desc ? raw[1..] : raw).Trim().ToLowerInvariant();
        if (!allowed.Contains(field)) { sort = new SortRequest(fallback, false); return false; }

        sort = new SortRequest(field, desc);
        return true;
    }
}

/// <summary>The sortable fields of policy query. Written out rather than reflected over the DTO, so adding a
/// property to a view cannot silently add a sortable column.</summary>
public static class PolicySortFields
{
    public const string Default = "policyno";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { "policyno", "effectivefrom", "effectiveto", "status", "membercount", "percentused" };
}

public static class MemberSortFields
{
    public const string Default = "memberno";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { "memberno", "effectivefrom", "effectiveto", "status", "relationship", "percentused", "consumed" };
}

/// <summary>Policy query's criteria (design 38 §4.4). Every field is optional; an all-null filter is a valid
/// "list everything I may see", which is what the payer scope predicate then narrows.</summary>
public sealed record PolicyQueryFilter(
    Guid? PayerId = null,
    Guid? PlanId = null,
    string? PlanLabel = null,
    PolicyStatus? Status = null,
    DateOnly? EffectiveOn = null,
    DateOnly? EffectiveFromAfter = null,
    DateOnly? EffectiveToBefore = null,
    Guid? GroupId = null,
    MemberCountBand? MemberCountBand = null,
    UtilizationBand? UtilizationBand = null,
    string? PolicyNo = null);

/// <summary>Member query's criteria (design 38 §4.4).</summary>
public sealed record MemberQueryFilter(
    Guid? PolicyId = null,
    Guid? PolicyPlanId = null,
    Guid? GroupId = null,
    Relationship? Relationship = null,
    EnrollmentStatus? Status = null,
    Guid? BranchId = null,
    DateOnly? EnrolledOn = null,
    DateOnly? EnrolledFromAfter = null,
    DateOnly? EnrolledToBefore = null,
    WaitingPeriodState? WaitingPeriod = null,
    UtilizationBand? UtilizationBand = null,
    string? MemberNo = null,
    /// <summary>Beneficiary ids already resolved from patient-service by identifier or name. Null = no identity
    /// filter was asked for; EMPTY = one was asked for and matched nobody, which must return no rows rather
    /// than every row.</summary>
    IReadOnlyList<Guid>? BeneficiaryIds = null);

/// <summary>One page of results plus the counts a caller needs to trust what they are looking at.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"page {Page}/{TotalPages} of {TotalCount}");
}
