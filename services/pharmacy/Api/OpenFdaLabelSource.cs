using Mersal.Ingredients;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Infrastructure;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// Retrieves manufacturer drug labels from openFDA, live, for the interaction and dosing checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>No PHI leaves the platform.</b> openFDA is a public United States government API and this is the only
/// place in the prescribing path that talks to a third party. The method signature is the guarantee: it
/// accepts an ingredient name and nothing else — no beneficiary, no encounter, no prescription, not even a
/// drug id — so there is no patient-identifying value in scope to leak, by accident or by later edit.
/// </para>
/// <para>
/// <b>Why it is cached hard.</b> A label changes a few times a year, and openFDA allows 1,000 requests a day
/// per IP without a key. A five-line prescription revalidated as the doctor types would exhaust a clinic's
/// entire daily allowance before lunch, at which point the check would fail for everyone. Successes are
/// cached for a day; failures and misses are cached briefly too, so a drug with no US label does not cost a
/// request on every keystroke.
/// </para>
/// <para>
/// <b>Why it degrades loudly.</b> Every failure — timeout, 429, 5xx, unparseable body — is recorded against
/// the drug as <c>Failed</c>, which the validator renders as <c>Unavailable</c>. The original brief ruled out
/// wiring an external interaction API because free ones vanish without notice (NLM's did, in January 2024).
/// That risk is real for openFDA too; what makes it acceptable here is that its disappearance shows up as
/// "check unavailable" on the prescriber's screen rather than as silence.
/// </para>
/// </remarks>
public sealed class OpenFdaLabelSource(
    IHttpClientFactory factory, IMemoryCache cache, TimeProvider clock, ILogger<OpenFdaLabelSource> log,
    IConfiguration config) : IDrugLabelSource
{
    /// <summary>
    /// openFDA publishes <c>snake_case</c> — <c>drug_interactions</c>, <c>generic_name</c>.
    /// </summary>
    /// <remarks>
    /// The platform's usual web defaults are camelCase, under which every field here binds to null: the
    /// lookup would then succeed, parse, match nothing, and report "no label published" for the entire
    /// catalogue — a total failure wearing the face of a coverage gap.
    /// </remarks>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>A label is a slow-moving regulatory document, not live data.</summary>
    private static readonly TimeSpan HitTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Short, because a miss can be transient — a label published tomorrow, or a rate limit that clears —
    /// and because caching "no" for a day would hide a recovery for a day.
    /// </summary>
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The whole label pass, not one request. A prescriber is waiting mid-consultation; past this the honest
    /// answer is "unavailable" rather than a spinner.
    /// </summary>
    /// <remarks>
    /// Measured rather than guessed, and the first measurement was wrong: openFDA answers a warm connection
    /// from the host in about 3 seconds, but the first call from a cold container — DNS, then a TLS handshake
    /// — overran a 4-second client timeout and every lookup reported unavailable. The generosity is
    /// affordable because it is paid once per ingredient per day: a hit is cached for 24 hours, so this is
    /// the cold-start cost, not the per-prescription cost.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <summary>openFDA answers in ~3s. More than a handful in flight is how a public API starts 429ing.</summary>
    private const int MaxConcurrency = 4;

    public async Task<Fetched<LabelEvidence>> FetchAsync(
        IReadOnlyList<DrugIngredient> drugs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drugs);

        var byDrug = new Dictionary<Guid, DrugLabelFact>();
        var unmatched = new Dictionary<Guid, string>();
        var failed = new Dictionary<Guid, string>();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Budget);

        using var gate = new SemaphoreSlim(MaxConcurrency);
        var gathered = new List<(Guid DrugId, LabelLookup Outcome)>();

        var work = drugs.DistinctBy(d => d.DrugId).Select(async drug =>
        {
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                return (drug.DrugId, await LookupAsync(drug, deadline.Token).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            gathered.AddRange(await Task.WhenAll(work).ConfigureAwait(false));
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or JsonException)
        {
            // The whole pass ran out of budget or the host is unreachable. Reporting per-drug failures here
            // would be guesswork about which ones got through, so the entire source reports unavailable.
            if (ct.IsCancellationRequested) throw;

            log.LogWarning(e, "openFDA label pass did not complete within {Budget}", Budget);
            return Fetched.NotAvailable<LabelEvidence>(
                "the manufacturer label service (openFDA) did not respond in time");
        }

        foreach (var (drugId, outcome) in gathered)
        {
            switch (outcome)
            {
                case LabelLookup.Found f: byDrug[drugId] = f.Fact with { DrugId = drugId }; break;
                case LabelLookup.NoLabel n: unmatched[drugId] = n.Reason; break;
                case LabelLookup.Error e: failed[drugId] = e.Reason; break;
            }
        }

        return Fetched.From(
            new LabelEvidence(byDrug, unmatched, failed),
            new ProvenanceInfo(
                "openFDA drug label (U.S. FDA)", "live", clock.GetUtcNow(),
                // Doc 43 §1 rule 2. Three limits, all of which change how much weight the advice deserves:
                // it is US labelling for an Egyptian formulary, it is prose rather than a curated matrix, and
                // silence from it is not a negative result.
                Caveat: "U.S. FDA product labelling, matched by active ingredient. Labels are narrative, not "
                    + "a complete interaction list, and describe U.S. products — an interaction or a strength "
                    + "may differ here or be absent from the text."));
    }

    private async Task<LabelLookup> LookupAsync(DrugIngredient drug, CancellationToken ct)
    {
        var candidates = IngredientTokens.Candidates(drug.ScientificName);
        if (candidates.Count == 0)
        {
            return new LabelLookup.NoLabel("no active ingredient is recorded for this product");
        }

        LabelLookup? lastError = null;
        LabelLookup? lastNoLabel = null;

        foreach (var candidate in candidates)
        {
            if (cache.TryGetValue(CacheKey(candidate), out LabelLookup? cached) && cached is not null)
            {
                if (cached is LabelLookup.Found) return cached;
                lastNoLabel = cached;
                continue;
            }

            var outcome = await QueryAsync(candidate, ct).ConfigureAwait(false);

            // Errors are not cached as answers — a rate limit today must not make the drug uncheckable for
            // the rest of the TTL.
            if (outcome is LabelLookup.Error) { lastError = outcome; continue; }

            cache.Set(CacheKey(candidate), outcome, outcome is LabelLookup.Found ? HitTtl : MissTtl);
            if (outcome is LabelLookup.Found) return outcome;
            lastNoLabel = outcome;
        }

        // A failure outranks a miss: "we could not find out" must not be reported as "there is no such
        // label". Otherwise keep the SPECIFIC reason the lookup gave — "matched products but none exactly"
        // tells a pharmacist the catalogue name is ambiguous and fixable, which the generic wording does not.
        return lastError ?? lastNoLabel
            ?? new LabelLookup.NoLabel($"no U.S. label is published under \"{candidates[0]}\"");
    }

    private async Task<LabelLookup> QueryAsync(string ingredient, CancellationToken ct)
    {
        var client = factory.CreateClient("openfda");
        var key = config["OpenFda:ApiKey"];

        // limit=5, not 1. The first result for "amoxicillin" is the amoxicillin/clavulanate combination
        // label, whose interactions section is not the plain product's — so several are fetched and only an
        // exact ingredient match is accepted.
        var url = $"/drug/label.json?search=openfda.generic_name:%22{Uri.EscapeDataString(ingredient)}%22&limit=5"
            + (string.IsNullOrWhiteSpace(key) ? "" : $"&api_key={Uri.EscapeDataString(key)}");

        try
        {
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            // openFDA answers a search that matches nothing with 404. That is an answer, not a failure.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new LabelLookup.NoLabel($"no U.S. label is published under \"{ingredient}\"");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                log.LogWarning("openFDA rate limit reached; set OpenFda:ApiKey to raise the daily quota");
                return new LabelLookup.Error("the manufacturer label service is rate-limited right now");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new LabelLookup.Error(
                    $"the manufacturer label service returned {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<OpenFdaResponse>(Json, ct).ConfigureAwait(false);
            return Select(ingredient, body);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException
                                      or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "openFDA lookup failed for {Ingredient}", ingredient);
            return new LabelLookup.Error("the manufacturer label service could not be reached");
        }
    }

    /// <summary>
    /// Accepts a result only when its generic name <b>is</b> the ingredient asked for, after normalisation.
    /// </summary>
    /// <remarks>
    /// The near-miss is the dangerous case, not the miss. Searching "chloride" returns benzalkonium chloride
    /// — a disinfectant — with a 200 and a full interactions section, and a client that took the first result
    /// would present that as this drug's label with complete confidence. "Not checked" is a far better answer
    /// than the wrong molecule's.
    /// </remarks>
    private static LabelLookup Select(string ingredient, OpenFdaResponse? body)
    {
        var results = body?.Results ?? [];

        foreach (var result in results)
        {
            var names = result.OpenFda?.GenericName ?? [];
            var exact = names.FirstOrDefault(n => IngredientTokens.IsExactMatch(ingredient, n));
            if (exact is null) continue;

            var aliases = IngredientTokens.Synonyms(ingredient)
                .Concat(IngredientTokens.Synonyms(exact))
                .Concat(result.OpenFda?.SubstanceName?.Select(s => s.ToLowerInvariant()) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new LabelLookup.Found(new DrugLabelFact(
                DrugId: Guid.Empty,
                SearchedIngredient: ingredient,
                MatchedGenericName: exact,
                Aliases: aliases,
                InteractionsText: Join(result.DrugInteractions),
                DosingText: Join(result.DosageAndAdministration),
                StrengthsText: Join(result.DosageFormsAndStrengths),
                LabelVersion: result.EffectiveTime ?? result.Id ?? "unknown"));
        }

        return results.Count > 0
            ? new LabelLookup.NoLabel(
                $"\"{ingredient}\" matched U.S. products but none exactly, so no label was used")
            : new LabelLookup.NoLabel($"no U.S. label is published under \"{ingredient}\"");
    }

    private static string? Join(IReadOnlyList<string>? parts)
        => parts is { Count: > 0 } ? string.Join("\n\n", parts) : null;

    private static string CacheKey(string ingredient) => $"openfda:label:{ingredient}";

    private abstract record LabelLookup
    {
        private LabelLookup() { }

        public sealed record Found(DrugLabelFact Fact) : LabelLookup;

        /// <summary>There is no such label. An answer — renders as NotChecked.</summary>
        public sealed record NoLabel(string Reason) : LabelLookup;

        /// <summary>We could not find out. NOT an answer — renders as Unavailable.</summary>
        public sealed record Error(string Reason) : LabelLookup;
    }

    private sealed record OpenFdaResponse(List<OpenFdaResult>? Results);

    private sealed record OpenFdaResult(
        string? Id,
        string? EffectiveTime,
        List<string>? DrugInteractions,
        List<string>? DosageAndAdministration,
        List<string>? DosageFormsAndStrengths,
        // "openfda", not "open_fda" — the one field in the payload that does not follow the snake_case rule,
        // and the block that carries the generic name every match is verified against.
        [property: JsonPropertyName("openfda")] OpenFdaBlock? OpenFda);

    private sealed record OpenFdaBlock(List<string>? GenericName, List<string>? SubstanceName);
}
