using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// What a prescription costs, and how it splits between the member and the payer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The split is not computed here.</b> It comes from <c>eligibility/check</c>, which composes it through
/// <c>libs/benefit-pricing</c> and <c>libs/money</c> — the same path claims adjudicates with. That library's
/// own header explains why: "the amount a receptionist reads off an eligibility card and the amount a claim
/// finally charges must be the same number… a refugee at a counter has no reviewer in the loop and no
/// recovery path". A second implementation living in pharmacy is precisely the drift it exists to prevent, so
/// this endpoint contributes the one thing eligibility cannot know — what the medicines cost — and asks for
/// the rest.
/// </para>
/// <para>
/// <b>Nothing is ever quoted at zero when it is unknown.</b> A missing price, an unresolvable tier, or a plan
/// version that does not price pharmacy all produce <c>determinate: false</c> and a reason. Zero at a
/// dispensing counter reads as "free", and a member told their medicine is free is told something the claim
/// will later contradict.
/// </para>
/// </remarks>
public static class RxPricing
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A clinical check must not hold the counter open; neither must a price.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static void MapRxPricing(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/prescriptions/{prescriptionId:guid}/pricing", async Task<IResult> (
            Guid prescriptionId, PharmacyDbContext db, IHttpClientFactory factory,
            IHbmpPrincipalAccessor me, HttpContext http,
            [FromQuery(Name = "dispense")] string[]? dispense, CancellationToken ct) =>
        {
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.PrescriptionId == prescriptionId, ct);
            if (rx is null)
            {
                return Results.Problem(statusCode: 404, title: "Not Found",
                    type: "https://mersal.foundation/problems/not-found");
            }

            // Gated exactly as the dispensable prescription view beside it is: `pharmacy:read`.
            //
            // Not tighter, and the first version of this was. It called the ABAC gate with
            // PharmacyPolicies.Dispense — a rule declared for `prescription_line`, not `prescription` — so
            // every request answered 403 "no-matching-rule": a policy that refuses everyone is not a strict
            // policy, it is a broken endpoint. Not looser either: this discloses the same prescription the
            // sibling endpoint already returns, plus what it costs.
            var bearer = http.Request.Headers.Authorization.FirstOrDefault();

            var lines = rx.Lines
                .Where(l => l.Status is Domain.RxLineStatus.Active or Domain.RxLineStatus.PartiallyDispensed
                            or Domain.RxLineStatus.Dispensed)
                .ToList();

            if (!DispenseBasis.TryParse(dispense, lines.Select(l => l.PrescriptionLineId), out var now,
                    out var basisError))
            {
                return Results.Problem(statusCode: 400, title: "Bad Request", detail: basisError,
                    type: "https://mersal.foundation/problems/validation");
            }

            var prices = await PricesAsync(factory, lines.Select(l => l.DrugId).Distinct().ToList(), bearer, ct);

            var lineViews = new List<RxLinePriceView>();
            decimal total = 0m;
            decimal basisAmount = 0m;
            var anyUnpriced = false;
            var basisUnpriced = false;

            foreach (var line in lines)
            {
                var unit = prices.TryGetValue(line.DrugId, out var p) ? p : null;
                // The TOTAL is what was PRESCRIBED, not what has been handed over so far. That tile answers
                // "what does this prescription cost" — a member deciding whether they can afford to collect
                // the rest needs the whole figure, not the part already paid for.
                var amount = unit is { } u ? u * line.QuantityPrescribed : (decimal?)null;
                if (amount is null) anyUnpriced = true; else total += amount.Value;

                // The BASIS is what is about to be handed over. A member paying for 7 of 14 is quoted on 7.
                if (now is not null && now.TryGetValue(line.PrescriptionLineId, out var q) && q > 0)
                {
                    if (unit is { } bu) basisAmount += bu * q; else basisUnpriced = true;
                }

                lineViews.Add(new RxLinePriceView(
                    line.PrescriptionLineId, line.DrugId, line.DrugName,
                    line.QuantityPrescribed, line.QuantityDispensed, unit, amount));
            }

            // No basis, or a basis of nothing, quotes the whole prescription. Quoting a zero basis would put
            // "Patient pays EGP 0.00" on screen before the pharmacist has entered anything, and a zero at a
            // dispensing counter reads as "free" — the one thing this endpoint may never say by accident.
            var onDispenseNow = now is not null && basisAmount > 0m;
            if (!onDispenseNow) { basisAmount = total; basisUnpriced = anyUnpriced; }

            if (basisUnpriced || (anyUnpriced && !onDispenseNow))
            {
                return Results.Ok(RxPricingView.Indeterminate(
                    lineViews,
                    "At least one medicine on this prescription has no list price, so the total cannot be "
                    + "stated. Quoting the priced lines alone would understate what the member owes.",
                    quotedOnDispenseNow: onDispenseNow));
            }

            var quote = await QuoteAsync(
                factory, rx.BeneficiaryId, me.Principal?.ProviderId, basisAmount, bearer, ct);

            // `total` is still null when some OTHER line has no price: the prescription total genuinely
            // cannot be stated, even though what is being handed over now can be.
            var totalView = anyUnpriced ? (decimal?)null : total;

            return Results.Ok(quote is null
                ? RxPricingView.Indeterminate(lineViews, QuoteUnavailable, totalView, basisAmount, onDispenseNow)
                : new RxPricingView(
                    lineViews, "EGP", totalView,
                    quote.MemberShare, quote.PayerShare,
                    Determinate: true, Reason: anyUnpriced ? PartlyUnpriced : null,
                    TierCode: quote.TierCode, IsCovered: quote.IsCovered,
                    QuotedOnEgp: basisAmount, QuotedOnDispenseNow: onDispenseNow));
        })
        .RequireAuthorization(HbmpPolicies.Scope("pharmacy:read"))
        .WithName("GetPrescriptionPricing");
    }

    private const string QuoteUnavailable =
        "The member's share could not be quoted — the plan does not price pharmacy at this provider's "
        + "network tier, or no tier could be resolved. The total above is the full list price.";

    private const string PartlyUnpriced =
        "The share below covers only what is being dispensed now. The prescription total cannot be stated "
        + "because another medicine on it has no list price.";

    private static async Task<Dictionary<Guid, decimal?>> PricesAsync(
        IHttpClientFactory factory, IReadOnlyList<Guid> drugIds, string? bearer, CancellationToken ct)
    {
        var result = new Dictionary<Guid, decimal?>();
        if (drugIds.Count == 0) return result;

        try
        {
            var body = await PostAsync<PricesDto>(
                factory, "masterdata", "/api/v1/drugs/prices/by-ids", new { drugIds }, bearer, ct);

            foreach (var item in body?.Items ?? []) result[item.DrugId] = item.PriceEgp;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException
                                      or InvalidOperationException)
        {
            // Leave the dictionary empty: every line then reports "no price", which is the honest reading of
            // "the catalogue did not answer". Defaulting to zero would price the prescription at nothing.
        }

        return result;
    }

    /// <summary>
    /// The member/payer split, from eligibility — never recomputed locally.
    /// </summary>
    /// <remarks>
    /// Returns null on ANY failure or indeterminate answer, which the caller renders as "cannot be quoted".
    /// The pharmacist's own token is forwarded, so the coverage read is attributed to the person who asked
    /// rather than to a service account.
    /// </remarks>
    private static async Task<Quote?> QuoteAsync(
        IHttpClientFactory factory, Guid beneficiaryId, string? providerId, decimal total,
        string? bearer, CancellationToken ct)
    {
        // No provider on the token, no quote. The cost share depends on the dispensing provider's network
        // tier, so a quote without one would be a different provider's price.
        if (!Guid.TryParse(providerId, out var provider)) return null;

        try
        {
            var body = await PostAsync<EligibilityCheckDto>(
                factory, "eligibility", "/api/v1/eligibility/check",
                new
                {
                    beneficiaryId,
                    benefitCategory = "PHARMACY",
                    providerId = provider,
                    estimatedAmount = total,
                },
                bearer, ct);

            var preview = body?.CostShare;
            if (preview is not { Determinate: true }) return null;
            if (preview.EstimatedMemberShare is not { } member || preview.EstimatedPayerShare is not { } payer)
            {
                return null;
            }

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
        {
            request.Headers.Authorization = auth;
        }

        using var response = await http.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<T>(Json, cts.Token);
    }

    private sealed record Quote(decimal MemberShare, decimal PayerShare, string? TierCode, bool IsCovered);

    /// <summary>
    /// The quantities about to be handed over, as <c>?dispense=&lt;lineId&gt;:&lt;qty&gt;</c>, repeated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An unknown line id is a 400, not a line quietly skipped.</b> Dropping one would quote the member a
    /// share for less than they are collecting, and it would look right — the tile would show a confident
    /// figure with no indication that a medicine had fallen out of it. A caller that sends a stale line id has
    /// a stale screen, and it must be told so rather than shown a smaller number.
    /// </para>
    /// <para>
    /// The same argument rules out clamping a quantity to what is left on the line: an over-quantity means the
    /// screen and the server disagree about what has already been dispensed, and the answer to that is to say
    /// so, not to invent the figure the caller probably meant.
    /// </para>
    /// </remarks>
    public static class DispenseBasis
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
                    error = $"'{entry}' is not a valid dispense basis. Expected '<lineId>:<quantity>'.";
                    return false;
                }

                if (quantity < 0)
                {
                    error = "A dispense quantity cannot be negative.";
                    return false;
                }

                if (!known.Contains(lineId))
                {
                    error = $"Line {lineId} is not an open line on this prescription.";
                    return false;
                }

                parsed[lineId] = parsed.TryGetValue(lineId, out var already) ? already + quantity : quantity;
            }

            basis = parsed;
            return true;
        }
    }

    private sealed record PricesDto(PriceItemDto[]? Items);
    private sealed record PriceItemDto(Guid DrugId, string? Name, decimal? PriceEgp);
    private sealed record EligibilityCheckDto(CostSharePreviewDto? CostShare);
    private sealed record CostSharePreviewDto(
        string? TierCode, bool IsCoveredAtTier, bool Determinate,
        decimal? EstimatedAllowedAmount, decimal? EstimatedMemberShare, decimal? EstimatedPayerShare);
}

/// <summary>One line's contribution to the total.</summary>
/// <param name="UnitPriceEgp">Null when the catalogue holds no price — NOT zero. See the note on the view.</param>
public sealed record RxLinePriceView(
    Guid PrescriptionLineId, Guid DrugId, string? DrugName,
    decimal QuantityPrescribed, decimal QuantityDispensed,
    decimal? UnitPriceEgp, decimal? LineTotalEgp);

/// <summary>
/// The three figures the counter shows, and whether they may be shown at all.
/// </summary>
/// <param name="Determinate">
/// False means the split could not be established. The member and payer figures are then NULL rather than
/// zero, and <paramref name="Reason"/> says why — a screen that rendered 0 here would tell a beneficiary
/// their medication is free.
/// </param>
/// <param name="QuotedOnEgp">
/// The amount the member/payer split was computed on. Equal to <paramref name="TotalEgp"/> when the caller
/// asked about the whole prescription, and equal to the value of what is about to be handed over when it
/// supplied a <c>?dispense=</c> basis.
/// </param>
/// <param name="QuotedOnDispenseNow">
/// True when the split answers "what does the patient pay for the quantities entered at the counter", false
/// when it answers "what does the patient pay if they collect all of it".
/// <para>
/// <b>Why the caller is told which question was answered.</b> The two figures differ, and a partial dispense
/// is the ordinary case rather than the exception — stock is short, or the member can only pay for part of a
/// course today. A tile that showed the whole-prescription share while the pharmacist was handing over half
/// of it would overstate what is owed at that moment, and a tile that silently switched between the two
/// would give the same label two meanings. The split itself is re-quoted rather than scaled, because
/// <c>libs/money</c> applies a deductible before a copay before coinsurance: the share of half a
/// prescription is not half the share of the whole one.
/// </para>
/// </param>
public sealed record RxPricingView(
    IReadOnlyList<RxLinePriceView> Lines,
    string Currency,
    decimal? TotalEgp,
    decimal? MemberShareEgp,
    decimal? PayerShareEgp,
    bool Determinate,
    string? Reason,
    string? TierCode = null,
    bool? IsCovered = null,
    decimal? QuotedOnEgp = null,
    bool QuotedOnDispenseNow = false)
{
    public static RxPricingView Indeterminate(
        IReadOnlyList<RxLinePriceView> lines, string reason, decimal? total = null,
        decimal? quotedOn = null, bool quotedOnDispenseNow = false) =>
        new(lines, "EGP", total, null, null, Determinate: false, reason,
            QuotedOnEgp: quotedOn, QuotedOnDispenseNow: quotedOnDispenseNow);
}
