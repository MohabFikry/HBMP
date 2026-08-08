using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>
/// What an investigation order costs, and how it splits between the member and the payer.
/// </summary>
/// <remarks>
/// <para>
/// The exact counterpart of pharmacy's <c>RxPricing</c>, deliberately: a lab bench and a dispensing counter
/// are the same situation — someone standing in front of a patient who is about to be told what they owe —
/// and the two must not answer differently. The split is NOT computed here. It comes from
/// <c>eligibility/check</c>, which composes it through <c>libs/benefit-pricing</c> and <c>libs/money</c>, the
/// same path claims adjudicates with. A second implementation living in orders is precisely the drift that
/// library exists to prevent.
/// </para>
/// <para>
/// <b>Nothing is ever quoted at zero when it is unknown.</b> A missing catalogue price, an unresolvable tier,
/// or a plan version that does not price LAB / IMAGING all produce <c>determinate: false</c> and a reason.
/// Zero at a counter reads as "free", and a member told their scan is free is told something the claim will
/// later contradict.
/// </para>
/// <para>
/// <b>Today every one of these tiles will say "cannot be quoted".</b> No examination in master data carries a
/// price and no plan version prices LAB or IMAGING. That is the mechanism working, not failing: the honest
/// state is stated, with the reason, until a real tariff and real benefit rules are authored.
/// </para>
/// </remarks>
public static class OrderPricing
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A price must not hold the bench open any more than a clinical check may.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static void MapOrderPricing(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/investigation-orders/{orderId:guid}/pricing", async Task<IResult> (
            Guid orderId, OrdersDbContext db, IHttpClientFactory factory,
            IHbmpPrincipalAccessor me, HttpContext http,
            [FromQuery(Name = "perform")] string[]? perform, CancellationToken ct) =>
        {
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null)
            {
                return Results.Problem(statusCode: 404, title: "Not Found",
                    type: "https://mersal.foundation/problems/not-found");
            }

            // Gated exactly as the fulfillment queue beside it is: `orders:read`. Not tighter — this
            // discloses the same order the sibling endpoint already returns, plus what it costs. Not looser.
            var bearer = http.Request.Headers.Authorization.FirstOrDefault();

            var lines = order.Lines
                .Where(l => l.Status is OrderLineStatus.Active or OrderLineStatus.PartiallyUsed
                            or OrderLineStatus.Completed)
                .ToList();

            if (!PerformBasis.TryParse(perform, lines.Select(l => l.OrderLineId), out var now, out var basisError))
            {
                return Results.Problem(statusCode: 400, title: "Bad Request", detail: basisError,
                    type: "https://mersal.foundation/problems/validation");
            }

            var prices = await PricesAsync(factory, lines.Select(l => l.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), bearer, ct);

            var lineViews = new List<OrderLinePriceView>();
            decimal total = 0m;
            decimal basisAmount = 0m;
            var anyUnpriced = false;
            var basisUnpriced = false;

            foreach (var line in lines)
            {
                var unit = prices.GetValueOrDefault(line.Code);
                // The TOTAL is what was ORDERED, not what has been performed so far. That tile answers "what
                // does this order cost" — a member deciding whether they can afford to come back for the rest
                // needs the whole figure, not the part already delivered.
                var amount = unit is { } u ? u * line.QuantityOrdered : (decimal?)null;
                if (amount is null) anyUnpriced = true; else total += amount.Value;

                // The BASIS is what is about to be performed. A member paying for one of three is quoted on one.
                if (now is not null && now.TryGetValue(line.OrderLineId, out var q) && q > 0)
                {
                    if (unit is { } bu) basisAmount += bu * q; else basisUnpriced = true;
                }

                lineViews.Add(new OrderLinePriceView(
                    line.OrderLineId, line.CodeSystem.ToString(), line.Code, line.Description,
                    line.QuantityOrdered, line.QuantityConsumed, unit, amount));
            }

            if (lines.Count == 0)
            {
                return Results.Ok(OrderPricingView.Indeterminate(lineViews,
                    "This order has no line still open, so there is nothing to price."));
            }

            // No basis, or a basis of nothing, quotes the whole order. Quoting a zero basis would put "Patient
            // pays EGP 0.00" on screen before the technician has entered anything, and a zero at a bench reads
            // as "free" — the one thing this endpoint may never say by accident.
            var onPerformNow = now is not null && basisAmount > 0m;
            if (!onPerformNow) { basisAmount = total; basisUnpriced = anyUnpriced; }

            if (basisUnpriced || (anyUnpriced && !onPerformNow))
            {
                return Results.Ok(OrderPricingView.Indeterminate(
                    lineViews,
                    "At least one examination on this order has no list price, so the total cannot be stated. "
                    + "Quoting the priced lines alone would understate what the member owes.",
                    quotedOnPerformNow: onPerformNow));
            }

            // `total` is still null when some OTHER line has no price: the order total genuinely cannot be
            // stated, even though what is being performed now can be.
            var totalView = anyUnpriced ? (decimal?)null : total;

            var category = BenefitCategoryMap.ForOrderType(order.OrderType);
            if (category is null)
            {
                return Results.Ok(OrderPricingView.Indeterminate(lineViews,
                    $"A {order.OrderType} order has no benefit category in the canonical set, so the member's "
                    + "share cannot be established. The total above is the full list price.", totalView,
                    basisAmount, onPerformNow));
            }

            var quote = await QuoteAsync(factory, order.BeneficiaryId, me.Principal?.ProviderId, category, basisAmount, bearer, ct);

            return Results.Ok(quote is null
                ? OrderPricingView.Indeterminate(lineViews, QuoteUnavailable, totalView, basisAmount, onPerformNow)
                : new OrderPricingView(
                    lineViews, "EGP", totalView,
                    quote.MemberShare, quote.PayerShare,
                    Determinate: true, Reason: anyUnpriced ? PartlyUnpriced : null,
                    TierCode: quote.TierCode, IsCovered: quote.IsCovered,
                    QuotedOnEgp: basisAmount, QuotedOnPerformNow: onPerformNow));
        })
        .RequireAuthorization(HbmpPolicies.Scope("orders:read"))
        .WithName("GetInvestigationOrderPricing");
    }

    private const string QuoteUnavailable =
        "The member's share could not be quoted — the plan does not price this examination category at this "
        + "provider's network tier, or no tier could be resolved. The total above is the full list price.";

    private const string PartlyUnpriced =
        "The share below covers only what is being performed now. The order total cannot be stated because "
        + "another examination on it has no list price.";

    private static async Task<Dictionary<string, decimal?>> PricesAsync(
        IHttpClientFactory factory, IReadOnlyList<string> codes, string? bearer, CancellationToken ct)
    {
        var result = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0) return result;

        try
        {
            var body = await PostAsync<PricesDto>(
                factory, "masterdata", "/api/v1/examination-types/prices/by-codes", new { codes }, bearer, ct);

            foreach (var item in body?.Items ?? [])
                if (item.Code is not null) result[item.Code] = item.PriceEgp;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException
                                      or InvalidOperationException)
        {
            // Leave the dictionary empty: every line then reports "no price", which is the honest reading of
            // "the catalogue did not answer". Defaulting to zero would price the order at nothing.
        }

        return result;
    }

    /// <summary>The member/payer split, from eligibility — never recomputed locally. Null on ANY failure or
    /// indeterminate answer, which the caller renders as "cannot be quoted".</summary>
    private static async Task<Quote?> QuoteAsync(
        IHttpClientFactory factory, Guid beneficiaryId, string? providerId, string benefitCategory,
        decimal total, string? bearer, CancellationToken ct)
    {
        // No provider on the token, no quote. The cost share depends on the performing provider's network
        // tier, so a quote without one would be a different provider's price.
        if (!Guid.TryParse(providerId, out var provider)) return null;

        try
        {
            var body = await PostAsync<EligibilityCheckDto>(
                factory, "eligibility", "/api/v1/eligibility/check",
                new { beneficiaryId, benefitCategory, providerId = provider, estimatedAmount = total },
                bearer, ct);

            var preview = body?.CostShare;
            if (preview is not { Determinate: true }) return null;
            if (preview.EstimatedMemberShare is not { } member || preview.EstimatedPayerShare is not { } payer)
                return null;

            return new Quote(member, payer, preview.TierCode, preview.IsCoveredAtTier);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException
                                      or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<T?> PostAsync<T>(
        IHttpClientFactory factory, string client, string path, object payload, string? bearer,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        var http = factory.CreateClient(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        if (!string.IsNullOrWhiteSpace(bearer) && AuthenticationHeaderValue.TryParse(bearer, out var auth))
            request.Headers.Authorization = auth;

        using var response = await http.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<T>(Json, cts.Token);
    }

    private sealed record Quote(decimal MemberShare, decimal PayerShare, string? TierCode, bool IsCovered);

    /// <summary>
    /// The quantities about to be performed, as <c>?perform=&lt;lineId&gt;:&lt;qty&gt;</c>, repeated. The exact
    /// counterpart of pharmacy's <c>DispenseBasis</c>, and refuses the same things for the same reasons: an
    /// unknown line id is a 400 rather than a line quietly skipped, because dropping one would quote the member
    /// a share for less than they are receiving and it would look right.
    /// </summary>
    public static class PerformBasis
    {
        public static bool TryParse(
            string[]? raw, IEnumerable<Guid> knownLineIds,
            out Dictionary<Guid, decimal>? basis, out string? error)
        {
            basis = null;
            error = null;
            if (raw is null || raw.Length == 0) return true;

            var known = knownLineIds.ToHashSet();
            var parsed = new Dictionary<Guid, decimal>();

            foreach (var entry in raw)
            {
                var sep = entry.LastIndexOf(':');
                if (sep <= 0
                    || !Guid.TryParse(entry.AsSpan(0, sep), out var lineId)
                    || !decimal.TryParse(entry.AsSpan(sep + 1), System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var quantity))
                {
                    error = $"'{entry}' is not a valid basis entry. Expected '<lineId>:<quantity>'.";
                    return false;
                }

                if (quantity < 0)
                {
                    error = "A quantity cannot be negative.";
                    return false;
                }

                if (!known.Contains(lineId))
                {
                    error = $"Line {lineId} is not an open line on this order.";
                    return false;
                }

                parsed[lineId] = parsed.TryGetValue(lineId, out var already) ? already + quantity : quantity;
            }

            basis = parsed;
            return true;
        }
    }

    private sealed record PricesDto(PriceItemDto[]? Items);
    private sealed record PriceItemDto(string? Code, string? Name, decimal? PriceEgp);
    private sealed record EligibilityCheckDto(CostSharePreviewDto? CostShare);
    private sealed record CostSharePreviewDto(
        string? TierCode, bool IsCoveredAtTier, bool Determinate,
        decimal? EstimatedAllowedAmount, decimal? EstimatedMemberShare, decimal? EstimatedPayerShare);
}

/// <summary>One line's contribution to the total.</summary>
/// <param name="UnitPriceEgp">Null when the catalogue holds no price — NOT zero. See the note on the view.</param>
public sealed record OrderLinePriceView(
    Guid OrderLineId, string CodeSystem, string Code, string? Description,
    decimal QuantityOrdered, decimal QuantityConsumed,
    decimal? UnitPriceEgp, decimal? LineTotalEgp);

/// <summary>
/// The three figures the bench shows, and whether they may be shown at all.
/// </summary>
/// <param name="Determinate">
/// False means the split could not be established. The member and payer figures are then NULL rather than
/// zero, and <paramref name="Reason"/> says why — a screen that rendered 0 here would tell a beneficiary
/// their scan is free.
/// </param>
/// <param name="QuotedOnEgp">
/// The amount the member/payer split was computed on — the whole order, or the value of what is about to be
/// performed when the caller supplied a <c>?perform=</c> basis.
/// </param>
/// <param name="QuotedOnPerformNow">
/// True when the split answers "what does the patient pay for the quantities entered at the bench", false
/// when it answers "what does the patient pay if the whole order is delivered". The split is re-quoted rather
/// than scaled, because <c>libs/money</c> applies a deductible before a copay before coinsurance: the share
/// of half an order is not half the share of the whole one.
/// </param>
public sealed record OrderPricingView(
    IReadOnlyList<OrderLinePriceView> Lines,
    string Currency,
    decimal? TotalEgp,
    decimal? MemberShareEgp,
    decimal? PayerShareEgp,
    bool Determinate,
    string? Reason,
    string? TierCode = null,
    bool? IsCovered = null,
    decimal? QuotedOnEgp = null,
    bool QuotedOnPerformNow = false)
{
    public static OrderPricingView Indeterminate(
        IReadOnlyList<OrderLinePriceView> lines, string reason, decimal? total = null,
        decimal? quotedOn = null, bool quotedOnPerformNow = false) =>
        new(lines, "EGP", total, null, null, Determinate: false, reason,
            QuotedOnEgp: quotedOn, QuotedOnPerformNow: quotedOnPerformNow);
}
