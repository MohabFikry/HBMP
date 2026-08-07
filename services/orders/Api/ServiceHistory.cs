using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>
/// 29.4 — ONE endpoint for "has this patient had this service before, and what did it show?" (design 45 §4).
///
/// <para><b>This is an AGGREGATION SURFACE, which is the shape that becomes a bypass.</b> Every previous
/// occurrence of one service, on one screen, is exactly the payload that quietly reveals what the results
/// inbox withholds. The rules design 45 §4 sets are therefore structural, not advisory:</para>
///
/// <list type="bullet">
/// <item><b>One endpoint, composed SERVER-SIDE under the caller's token.</b> A withheld field is ABSENT from
/// the JSON — never present-but-hidden, because a client that receives it has received it.</item>
/// <item><b>The sensitivity gate still binds.</b> A restricted result renders existence-only, through the SAME
/// <see cref="SensitiveDisclosure"/> rule the results inbox uses. Not a re-implementation of it — a
/// re-implementation is how the two come to disagree, and the disagreement would only ever be discovered by
/// someone reading a mental-health result they should not have.</item>
/// <item><b>Intersection, never union.</b> Treating relationship AND branch scope AND provider ownership all
/// still apply. A history modal that answered "the caller passes ANY of these" would be a new access path
/// wearing an old one's clothes.</item>
/// <item><b>Three states, distinctly.</b> has-history / no-previous-occurrences / could-not-load. The last
/// must never render as the second: a clinician reading "no previous tests" when the service was simply
/// unreachable will re-order unnecessarily, or miss a trend.</item>
/// </list>
/// </summary>
public static class ServiceHistoryEndpoints
{
    public static void MapServiceHistory(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/patients").RequireAuthorization();

        v1.MapGet("/{beneficiaryId:guid}/service-history", async (
            Guid beneficiaryId, string? serviceType, string? code, int? page, int? pageSize,
            HttpRequest http, OrdersDbContext db, OrdersGate gate, IAuditClient audit,
            IHbmpPrincipalAccessor me, BranchScopeState branch, TimeProvider clock, CancellationToken ct) =>
        {
            var bearer = http.Headers.Authorization.ToString();

            // GATE ONE — the treating relationship, the SAME check order creation makes. Asked first and
            // asked here, because this endpoint reaches a patient's whole history of a service and the
            // question "may this clinician look at this patient at all" is prior to every narrowing below.
            var denied = await gate.CheckAsync(OrdersPolicies.Read, null, beneficiaryId, bearer, ct);
            if (denied is not null) return denied;

            var (p, ps) = (page is null or < 1 ? 1 : page.Value, pageSize is null or < 1 or > 100 ? 25 : pageSize.Value);

            var query = db.Orders.AsNoTracking().Include(o => o.Lines)
                .Where(o => o.BeneficiaryId == beneficiaryId);

            // GATE TWO — branch scope, through the SHARED helper the clinician worklist uses. INTERSECTED
            // with the treating gate above, never offered as an alternative to it. Reusing ApplyBranchScope
            // rather than writing the predicate here is the whole point: a second expression of "which
            // branches may this caller see" is a second thing to keep in step, and this endpoint is precisely
            // where a looser copy would go unnoticed.
            query = query.ApplyBranchScope(
                o => o.OrderingBranchId,
                me.Principal is null ? ScopeMode.MemberScoped : BranchScopeModes.ModeFor(me.Principal),
                branch.Context);

            if (!string.IsNullOrWhiteSpace(serviceType) && OrderTypes.TryParse(serviceType, out var type))
            {
                // Both spellings collapse to the canonical one BEFORE the comparison, so a history query for
                // Radiology still finds orders written as Imaging before the 29.1 switch.
                query = query.Where(o => o.OrderType == type || (type == OrderType.Radiology && o.OrderType == OrderType.Imaging));
            }

            var trimmed = code?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                query = query.Where(o => o.Lines.Any(l => l.Code == trimmed));

            var orders = await query.OrderByDescending(o => o.RequestedAt).Take(500).ToListAsync(ct);

            var rows = new List<ServiceHistoryRow>();
            foreach (var order in orders)
            {
                foreach (var line in order.Lines.Where(l => trimmed is null || l.Code == trimmed))
                {
                    var result = await db.Fulfillments.AsNoTracking()
                        .Where(f => f.OrderLineId == line.OrderLineId)
                        .OrderByDescending(f => f.ConsumedAt).FirstOrDefaultAsync(ct);

                    // GATE THREE — the sensitivity gate, through the shared rule. `callerHasAccess` is the
                    // author-or-active-grant fact, computed here rather than assumed: the ordering clinician
                    // sees their own restricted result, everyone else sees that it exists.
                    var isAuthor = order.CreatedBy is { } author
                                   && string.Equals(author, me.Principal?.Subject, StringComparison.Ordinal);
                    var hasGrant = await db.ReportAccessGrants.AsNoTracking().AnyAsync(
                        g => g.OrderLineId == line.OrderLineId
                             && g.GranteeUserId == me.Principal!.Subject
                             && g.RevokedAt == null && g.ExpiresAt > clock.GetUtcNow(), ct);

                    var restricted = SensitiveDisclosure.IsRestricted(
                        line.SensitivityLevel.ToString(), callerHasAccess: isAuthor || hasGrant);

                    rows.Add(new ServiceHistoryRow(
                        OrderId: order.OrderId,
                        OrderNo: order.OrderNo,
                        OrderLineId: line.OrderLineId,
                        ServiceType: OrderTypes.Canonical(order.OrderType).ToString(),
                        CodeSystem: line.CodeSystem.ToString(),
                        Code: line.Code,
                        Description: line.Description,
                        OccurredAt: order.RequestedAt,
                        Status: line.Status.ToString(),
                        ActorUserId: order.CreatedBy,
                        BranchId: order.OrderingBranchId,
                        Restricted: restricted,
                        SensitivityLevel: line.SensitivityLevel.ToString(),
                        // THE conditional field, and the only one. A restricted row carries no value and no
                        // numeric — the fields are ABSENT, so there is nothing for a client to reveal.
                        ResultSummary: restricted ? null : result?.ResultValue,
                        NumericValue: restricted ? null : ParseNumeric(result?.ResultValue)));
                }
            }

            // Every open is an audited PHI read NAMING the patient and the service.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "service_history",
                EntityId = $"{beneficiaryId}/{serviceType ?? "*"}/{trimmed ?? "*"}",
                Action = AuditAction.Read, ActorUserId = me.Principal?.Subject,
                DecisionOutcome = "Allow", DecisionReasonCode = $"service-history:{rows.Count}",
                FieldClasses = ["phi"],
            }, ct);

            var page1 = rows.Skip((p - 1) * ps).Take(ps).ToList();
            return Results.Ok(new ServiceHistoryResponse(
                BeneficiaryId: beneficiaryId,
                ServiceType: serviceType,
                Code: trimmed,
                Total: rows.Count,
                Page: p,
                PageSize: ps,
                // The TREND is the clinical point of the feature — but only over rows the caller may actually
                // see. Computing it across restricted rows would leak their values through an average.
                Trend: page1.Where(r => r is { Restricted: false, NumericValue: not null })
                    .OrderBy(r => r.OccurredAt)
                    .Select(r => new TrendPoint(r.OccurredAt, r.NumericValue!.Value))
                    .ToList(),
                Items: page1));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));
    }

    /// <summary>A result value as a number, or null when it is not one. Deliberately conservative: "Positive",
    /// "&lt;0.01" and "see report" are not numbers, and coercing them to 0 would draw a trend line through
    /// points that do not exist.</summary>
    private static decimal? ParseNumeric(string? value) =>
        decimal.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}

/// <summary>One previous occurrence of a service. Withheld fields are ABSENT, never null-but-declared-visible.</summary>
public sealed record ServiceHistoryRow(
    Guid OrderId, string OrderNo, Guid OrderLineId, string ServiceType, string CodeSystem, string Code,
    string? Description, DateTimeOffset OccurredAt, string Status, string? ActorUserId, Guid? BranchId,
    /// <summary>True ⇒ this row is EXISTENCE ONLY: date, service, actor, branch and this marker, and nothing
    /// else. The request-access action is how the caller asks for more (design 37 §6).</summary>
    bool Restricted,
    string? SensitivityLevel,
    /// <summary>The result, and its numeric form where it HAS one. No unit field: order_fulfillment does not
    /// store one, and inventing a unit to display beside a number is how a trend comes to be read in the
    /// wrong scale. The value is shown verbatim beside the chart, and the data table stays in the DOM
    /// alongside it (design 12 §7).</summary>
    string? ResultSummary, decimal? NumericValue);

/// <summary>A point on the trend. Only ever built from rows the caller may see.</summary>
public sealed record TrendPoint(DateTimeOffset At, decimal Value);

public sealed record ServiceHistoryResponse(
    Guid BeneficiaryId, string? ServiceType, string? Code, int Total, int Page, int PageSize,
    IReadOnlyList<TrendPoint> Trend, IReadOnlyList<ServiceHistoryRow> Items);
