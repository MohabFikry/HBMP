using System.Globalization;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.4 — utilization for a member, group, plan, policy or payer (design 38 §4.3).
///
/// <para>READ-ONLY, and structurally so: there is no write path in this file and no code path that can touch
/// <c>coverage_limit.consumed_value</c>. Phase 18 owns the accumulator; a report that could move it would be a
/// second writer to the one number the whole benefit spine is arbitrated by.</para>
///
/// <para>Every scope funnels through ONE aggregation path — resolve the member set, then sum the same
/// accumulator rows the member view reports. Five separate sums would be five chances for the group total to
/// disagree with the members it is made of, and the disagreement would surface as a member being refused care
/// their own report says they are entitled to.</para>
/// </summary>
public static class UtilizationEndpoints
{
    public static void MapUtilization(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1/utilization")
            .RequireAuthorization(HbmpPolicies.Scope("policy:read"));

        MapMember(read);
        MapScope(read, "/groups/{id:guid}", UtilizationScope.Group);
        MapScope(read, "/plans/{id:guid}", UtilizationScope.Plan);
        MapScope(read, "/policies/{id:guid}", UtilizationScope.Policy);
        MapScope(read, "/payers/{id:guid}", UtilizationScope.Payer);
        MapExport(read);
    }

    // ---- Individual ------------------------------------------------------------------------------------

    private static void MapMember(RouteGroupBuilder read)
    {
        read.MapGet("/members/{beneficiaryId:guid}", async (
            Guid beneficiaryId, DateOnly? from, DateOnly? to,
            UtilizationQuery query, UtilizationFactComposer facts, PolicyGate gate,
            IAuditClient audit, IBusinessCalendar calendar, HttpContext http, CancellationToken ct) =>
        {
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var (windowFrom, windowTo, asOf) = Window(from, to, calendar);
            var token = Bearer(http);

            // The membership is looked up rather than defaulted: a utilization card with a blank member number
            // is unusable at a counter, where the member number is the only handle anyone has on the person.
            // Terminated memberships are included — their consumption happened and still has to be readable.
            var memberships = await query.MembersAsync(
                UtilizationScope.Member, beneficiaryId, includeInactive: true, ct);
            var membership = memberships.Count > 0 ? memberships[0] : null;

            var accumulators = await query.MemberAccumulatorsAsync(beneficiaryId, asOf, ct);
            var activity = await query.ActivityAsync([beneficiaryId], windowFrom, windowTo, ct);
            var tiers = await query.TierSplitAsync([beneficiaryId], windowFrom, windowTo, token, ct);
            var external = await facts.ComposeAsync(
                new UtilizationFactWindow([beneficiaryId], windowFrom, windowTo), token, ct);

            var activityByCategory = activity.ToDictionary(a => a.BenefitCategoryCode, StringComparer.Ordinal);
            var categories = accumulators
                .Select(a => CategoryUtilizationView.From(
                    a, activityByCategory.GetValueOrDefault(a.BenefitCategoryCode)))
                .ToList();

            // Reconciliation along a second, independent path: SUM(consumed_value) straight from SQL versus
            // the figures actually rendered. They must be equal; the response says whether they were.
            var accumulatorTotal = await query.AccumulatorTotalAsync([beneficiaryId], asOf, ct);
            var reported = categories.Sum(c => c.Consumed);

            var view = new MemberUtilizationView(
                beneficiaryId, membership?.EnrollmentId ?? Guid.Empty, membership?.MemberNo ?? "",
                asOf, windowFrom, windowTo,
                categories,
                [.. tiers.Select(TierUtilizationView.From)],
                UtilizationProjection.Project(ExternalUtilizationView.From(external), principal.Roles),
                ReconciliationView.Of(accumulatorTotal, reported));

            // A member's utilization names their benefit consumption — a PHI-adjacent read even though no
            // clinical value is in the payload. Audited on every call, per 19-audit-strategy.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "utilization", EntityId = $"member:{beneficiaryId}", Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = view.Reconciliation.Reconciled ? "reconciled" : "RECONCILIATION-MISMATCH",
                DecisionReasonCode = $"window:{Iso(windowFrom)}..{Iso(windowTo)}",
                FieldClasses = ["coverage"],
            }, ct);

            return Results.Ok(view);
        });
    }

    // ---- Group · plan · policy · payer ------------------------------------------------------------------

    private static void MapScope(RouteGroupBuilder read, string route, UtilizationScope scope)
    {
        read.MapGet(route, async (
            Guid id, DateOnly? from, DateOnly? to, decimal? outlierThresholdPercent, bool? includeInactive,
            UtilizationQuery query, UtilizationFactComposer facts, PolicyGate gate,
            IAuditClient audit, IBusinessCalendar calendar, HttpContext http, CancellationToken ct) =>
        {
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var threshold = outlierThresholdPercent ?? UtilizationMath.DefaultOutlierThresholdPercent;
            if (threshold is < 0m or > 1000m)
                return ProblemResults.Invalid("INVALID_THRESHOLD", "outlierThresholdPercent must be between 0 and 1000.");

            var (windowFrom, windowTo, asOf) = Window(from, to, calendar);
            var token = Bearer(http);

            // 19.2b — a policy with several plans must be comparable plan-by-plan, which is why Plan is a
            // first-class scope and not a filter on the policy view: "which plan is consuming
            // disproportionately" is unanswerable from a total.
            var members = await query.MembersAsync(scope, id, includeInactive ?? false, ct);
            var beneficiaryIds = members.Select(m => m.BeneficiaryId).Distinct().ToList();

            var totals = await query.MemberTotalsAsync(members, asOf, ct);
            var (limit, consumed, remaining, percent) = UtilizationMath.Roll(totals);

            var tiers = await query.TierSplitAsync(beneficiaryIds, windowFrom, windowTo, token, ct);
            var external = await facts.ComposeAsync(
                new UtilizationFactWindow(beneficiaryIds, windowFrom, windowTo), token, ct);

            var accumulatorTotal = await query.AccumulatorTotalAsync(beneficiaryIds, asOf, ct);

            var view = new ScopeUtilizationView(
                scope.ToString(), id, asOf, windowFrom, windowTo,
                members.Count, limit, consumed, remaining, percent, threshold,
                [.. totals.Select(MemberRowView.From)],
                [.. UtilizationMath.Outliers(totals, threshold).Select(MemberRowView.From)],
                [.. UtilizationMath.Distribution(totals).Select(DistributionBucketView.From)],
                [.. tiers.Select(TierUtilizationView.From)],
                UtilizationProjection.Project(ExternalUtilizationView.From(external), principal.Roles),
                ReconciliationView.Of(accumulatorTotal, consumed));

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "utilization", EntityId = $"{scope}:{id}", Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = view.Reconciliation.Reconciled ? "reconciled" : "RECONCILIATION-MISMATCH",
                DecisionReasonCode = $"members:{members.Count};window:{Iso(windowFrom)}..{Iso(windowTo)}",
                FieldClasses = ["coverage"],
            }, ct);

            return Results.Ok(view);
        });
    }

    // ---- Export ----------------------------------------------------------------------------------------

    private static void MapExport(RouteGroupBuilder read)
    {
        // Column-allow-listed. There is no clinical column to omit — the payload never had one — but the
        // allow-list is written out rather than reflected over the DTO so that adding a field to the DTO
        // cannot silently add a column to everyone's spreadsheet.
        read.MapGet("/export", async (
            string scope, Guid scopeId, DateOnly? from, DateOnly? to,
            UtilizationQuery query, PolicyGate gate, IAuditClient audit,
            IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            if (!Enum.TryParse<UtilizationScope>(scope, ignoreCase: true, out var parsed))
                return ProblemResults.Invalid("UNKNOWN_SCOPE", $"'{scope}' is not a utilization scope.");

            var (windowFrom, windowTo, asOf) = Window(from, to, calendar);

            var members = parsed == UtilizationScope.Member
                ? await query.MembersAsync(UtilizationScope.Member, scopeId, includeInactive: true, ct)
                : await query.MembersAsync(parsed, scopeId, includeInactive: false, ct);
            var totals = await query.MemberTotalsAsync(members, asOf, ct);

            var csv = new System.Text.StringBuilder();
            csv.AppendLine("memberNo,policyPlanId,groupId,totalLimit,totalConsumed,totalRemaining,percentUsed,unlimited");
            foreach (var m in totals)
            {
                csv.Append(Csv(m.MemberNo)).Append(',')
                   .Append(m.PolicyPlanId).Append(',')
                   .Append(m.GroupId?.ToString() ?? "").Append(',')
                   .Append(Num(m.TotalLimit)).Append(',')
                   .Append(Num(m.TotalConsumed)).Append(',')
                   .Append(Num(m.TotalRemaining)).Append(',')
                   .Append(m.PercentUsed is { } p ? Num(p) : "").Append(',')
                   .Append(m.AnyUnlimited ? "true" : "false").AppendLine();
            }

            // An export leaves the platform's controls behind and becomes a file on somebody's laptop, so it
            // is audited with the row count — the number a later investigation needs and cannot recover.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "utilization", EntityId = $"{parsed}:{scopeId}", Action = AuditAction.Export,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = $"rows={totals.Count}",
                DecisionReasonCode = $"window:{Iso(windowFrom)}..{Iso(windowTo)}",
                FieldClasses = ["coverage"],
            }, ct);

            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
                "text/csv", $"utilization-{parsed}-{scopeId}-{Iso(windowFrom)}-{Iso(windowTo)}.csv");
        });
    }

    // ---- Shared ----------------------------------------------------------------------------------------

    /// <summary>
    /// The reporting window and the as-of date.
    ///
    /// <para>Two dates, not one, and they answer different questions. <c>asOf</c> is TODAY, because the
    /// accumulator is a live balance and there is no historical version of it to read — asking "what was
    /// consumed as of last March" of a value that has since been reset returns this period's number wearing
    /// last March's label. The window bounds the LEDGER activity, which genuinely is historical.</para>
    ///
    /// <para>Defaults to the last 90 Cairo days, matching the claims KPI default so two reports opened side by
    /// side cover the same period.</para>
    /// </summary>
    private static (DateOnly From, DateOnly To, DateOnly AsOf) Window(
        DateOnly? from, DateOnly? to, IBusinessCalendar calendar)
    {
        var today = calendar.Today();   // 18.A3 — Cairo days; a report opened at 23:30 local must include today
        var windowTo = to ?? today;
        var windowFrom = from ?? windowTo.AddDays(-90);
        return (windowFrom, windowTo, today);
    }

    private static string? Bearer(HttpContext http) => http.Request.Headers.Authorization.FirstOrDefault();

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Num(decimal d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Csv(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
