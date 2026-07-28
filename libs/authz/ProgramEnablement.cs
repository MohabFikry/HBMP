using Microsoft.AspNetCore.Http;

namespace Mersal.Authz;

/// <summary>The per-tenant programme switches (design 40 §4). Keys mirror admin.tenant_feature's CHECK.</summary>
public static class ProgramFeatures
{
    public const string Claims = "claims";
    public const string CallCentre = "callcentre";
    public const string Interop = "interop";
    public const string ReportingExtracts = "reporting_extracts";
    public const string Pharmacy = "pharmacy";
    public const string Orders = "orders";
    public const string Approvals = "approvals";
    public const string Emr = "emr";
    public const string Finance = "finance";
    public const string Documents = "documents";
    public const string CaseManagement = "case_management";
}

/// <summary>The per-tenant caps (design 40 §4). Keys mirror admin.tenant_limit's CHECK.</summary>
public static class ProgramLimits
{
    public const string ActiveUsers = "active_users";
    public const string ActiveProviderUsers = "active_provider_users";
    public const string MonthlyExtracts = "monthly_extracts";
    public const string StorageMb = "storage_mb";
}

/// <summary>
/// The THIRD, ORTHOGONAL gate (design 40 §4, adaptation A4): checked AFTER authorization and BEFORE
/// execution, so a fully authorized principal can still be refused because their organisation is not on
/// the programme.
///
/// The reason this is its own type rather than another 403 is the remedy. "You lack the permission" is
/// answered by the tenant's own administrator; "your organisation is not enabled for this" is answered by
/// Mersal programme administration. Returning one for the other sends someone to the wrong person, and the
/// SPA shows the wrong copy — which under A4 matters especially, because this must never read as a paywall
/// to a partner NGO.
///
/// ENABLEMENT NEVER GRANTS. A switched-on feature still requires the permission; this gate can only ever
/// subtract. It is a separate check precisely so nobody can reach for it as a way to hand out access.
/// </summary>
public static class ProgramEnablement
{
    /// <summary>The problem `type` for a disabled programme. DISTINCT from the authorization denial's type —
    /// the SPA keys its "not enabled — contact Mersal programme administration" treatment off this.</summary>
    public const string NotEnabledType = "https://mersal.foundation/problems/program-not-enabled";

    /// <summary>The problem `type` for a breached cap.</summary>
    public const string LimitReachedType = "https://mersal.foundation/problems/program-limit-reached";

    public const string NotEnabledCode = "program-not-enabled";
    public const string LimitReachedCode = "program-limit-reached";

    /// <summary>
    /// Whether a feature is on for a tenant. ABSENCE MEANS DISABLED — a programme nobody has switched on
    /// has not been switched on, and defaulting the other way would enable every module for every tenant
    /// the moment this table shipped empty.
    /// </summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, bool> features, string featureKey)
    {
        ArgumentNullException.ThrowIfNull(features);
        return features.TryGetValue(featureKey, out var on) && on;
    }

    /// <summary>
    /// 403 for a programme that is not enabled. Carries the feature key so the SPA can name the programme
    /// rather than showing a generic wall, and so support can act on the ticket without a follow-up.
    /// </summary>
    public static IResult NotEnabled(string featureKey) => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "program-not-enabled",
        type: NotEnabledType,
        detail: "This module is not enabled for your organization. Contact Mersal programme administration.",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = NotEnabledCode,
            ["feature"] = featureKey,
        });

    /// <summary>
    /// 403 for a breached cap. Reports the limit and the live count, because "you are at your limit" without
    /// the numbers is not actionable — an administrator needs to know whether to free a slot or ask for more.
    /// </summary>
    public static IResult LimitReached(string limitKey, int max, int current) => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "program-limit-reached",
        type: LimitReachedType,
        detail: $"This organization has reached its configured limit for {limitKey} ({current}/{max}). " +
                "Free a slot, or contact Mersal programme administration to raise it.",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = LimitReachedCode,
            ["limit"] = limitKey,
            ["max"] = max,
            ["current"] = current,
        });

    /// <summary>
    /// Whether one more row would breach a cap, given a LIVE count taken inside the mutating transaction.
    ///
    /// <paramref name="max"/> is null when no cap is configured, which means UNLIMITED — inventing a default
    /// limit would take a working platform offline the day the table shipped empty. Note the boundary: a cap
    /// of N permits exactly N rows, so the check is on the count that WOULD result.
    /// </summary>
    public static bool WouldBreach(int? max, int liveCount) => max is { } m && liveCount + 1 > m;
}
