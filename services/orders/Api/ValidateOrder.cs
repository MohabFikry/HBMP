using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>
/// Step 1 of ordering an investigation: advisory checks while the clinician is still composing.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>POST /prescriptions/validate</c>, and it carries the same warning: <b>its verdict is
/// display state only</b>. The order-creation path re-derives everything from current state and reads nothing
/// this returned, so a client that lied about the outcome changes nothing about what gets written.
/// </para>
/// <para>
/// It exists so a doctor finds out about an unknown code, a repeat test or a pre-authorization BEFORE
/// pressing submit — the modal it replaces offered a text box, hard-coded defaults and a 422.
/// </para>
/// </remarks>
public static class ValidateOrderEndpoints
{
    public static void MapValidateOrder(this WebApplication app)
    {
        app.MapPost("/api/v1/investigation-orders/validate", async (
            ValidateOrderRequest req, HttpRequest http, OrdersDbContext db, OrdersGate gate,
            ICodeValidator codes, OrderRoutingOptions routing, IHbmpPrincipalAccessor me,
            CancellationToken ct) =>
        {
            var bearer = http.Headers.Authorization.ToString();
            var denied = await gate.CheckAsync(OrdersPolicies.Create, null, req.BeneficiaryId, bearer, ct);
            if (denied is not null) return denied;

            var lines = (req.Lines ?? [])
                .Select(l => new InvestigationLineInput(l.LineId, l.Code, l.Description, l.Quantity))
                .ToList();
            if (lines.Count == 0)
                return Results.Problem(statusCode: 400, title: "an order must have at least one line",
                    type: "urn:hbmp:empty-order");

            // Master data, once per distinct code. NULL — not an empty set — when the catalogue could not be
            // reached: "no codes are known" and "we could not ask" produce opposite findings, and collapsing
            // them would report every line as an unknown code during a masterdata outage.
            var distinct = lines.Select(l => l.Code?.Trim()).Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            HashSet<string>? known = new(StringComparer.OrdinalIgnoreCase);
            foreach (var code in distinct)
            {
                try
                {
                    if (await codes.IsValidAsync(CodeSystem.CPT, code, bearer, ct)) known.Add(code);
                }
                catch (HttpRequestException) { known = null; break; }
                catch (TaskCanceledException) { known = null; break; }
            }

            // What this patient already has outstanding. Their OWN orders only, and only the codes — no
            // result, no report, nothing this screen has no business showing.
            var open = await db.Orders.AsNoTracking()
                .Where(o => o.BeneficiaryId == req.BeneficiaryId
                            && (o.Status == OrderStatus.Requested || o.Status == OrderStatus.PendingApproval
                                || o.Status == OrderStatus.Approved || o.Status == OrderStatus.Active
                                || o.Status == OrderStatus.PartiallyUsed))
                .SelectMany(o => o.Lines.Where(l => l.Status == OrderLineStatus.Active).Select(l => l.Code))
                .Distinct()
                .ToListAsync(ct);

            var orderType = Enum.TryParse<OrderType>(req.OrderType, ignoreCase: true, out var ot) ? ot : OrderType.Lab;
            var snapshot = new InvestigationSnapshot(
                known,
                new HashSet<string>(open, StringComparer.OrdinalIgnoreCase),
                routing.GatedCodes,
                (req.DiagnosisIcdCodes ?? []).Count);

            var findings = InvestigationChecks.Evaluate(orderType, lines, snapshot);
            var byLine = findings.GroupBy(f => f.LineId)
                .ToDictionary(g => g.Key, g => InvestigationChecks.StateOf(g).ToString());

            return Results.Ok(new ValidateOrderResponse(
                Guid.NewGuid(),
                InvestigationChecks.StateOf(findings).ToString(),
                findings.Select(f => new OrderFindingView(
                    f.LineId, f.Kind.ToString(), f.State.ToString(), f.MessageEn, f.MessageAr,
                    f.RequiresAcknowledgement, f.IsBlocking, f.SourceName, f.Caveat)).ToList(),
                byLine));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"))
        .Produces<IEnumerable<OrderFindingView>>();
    }
}

public sealed record ValidateOrderLine(Guid LineId, string? Code, string? Description, decimal Quantity);

public sealed record ValidateOrderRequest(
    Guid BeneficiaryId, Guid EncounterId, string OrderType,
    List<ValidateOrderLine>? Lines, List<string>? DiagnosisIcdCodes);

public sealed record OrderFindingView(
    Guid LineId, string Kind, string State, string MessageEn, string MessageAr,
    bool RequiresAcknowledgement, bool IsBlocking, string? SourceName, string? Caveat);

public sealed record ValidateOrderResponse(
    Guid ValidationId, string OverallState,
    IReadOnlyList<OrderFindingView> Findings,
    IReadOnlyDictionary<Guid, string> LineStates);
