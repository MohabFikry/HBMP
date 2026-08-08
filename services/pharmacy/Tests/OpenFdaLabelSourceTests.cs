using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Api;
using Mersal.Pharmacy.Infrastructure;
using Xunit;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// The live manufacturer-label lookup, exercised entirely offline.
/// </summary>
/// <remarks>
/// These never touch the network. A test that called openFDA for real would be measuring the FDA's uptime
/// rather than this code, would fail in CI behind a proxy, and would spend the platform's daily request
/// allowance on every run.
/// </remarks>
public class OpenFdaLabelSourceTests
{
    private static readonly Guid DrugId = Guid.NewGuid();

    [Fact]
    public async Task Sends_only_an_ingredient_name_to_the_external_service()
    {
        var handler = new RecordingHandler(Label("WARFARIN SODIUM"));
        var source = Source(handler);

        await source.FetchAsync([new DrugIngredient(DrugId, "Warfarin Sodium")]);

        // openFDA is a public U.S. government API and the only third party in the prescribing path. The
        // request must carry a molecule name and nothing else — not the beneficiary, not the encounter, and
        // not even the internal drug id, which is a stable identifier that would accumulate into a profile of
        // what a given clinic prescribes.
        var url = handler.Requests.Single();
        url.Should().Contain("warfarin");
        url.Should().NotContain(DrugId.ToString());
        handler.Requests.Should().NotContainMatch("*beneficiar*");
    }

    [Fact]
    public async Task A_404_means_there_is_no_such_label_which_is_an_answer()
    {
        var source = Source(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var fetched = await source.FetchAsync([new DrugIngredient(DrugId, "obscure herbal tonic")]);

        var evidence = Available(fetched);
        // openFDA answers a search that matched nothing with 404. That is a fact about the world, not a
        // failure of the lookup, and it renders as NotChecked with the reason named.
        evidence.Unmatched.Should().ContainKey(DrugId);
        evidence.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rate_limit_is_a_failure_and_never_a_clean_result()
    {
        var source = Source(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var fetched = await source.FetchAsync([new DrugIngredient(DrugId, "warfarin")]);

        var evidence = Available(fetched);
        // The distinction the whole design rests on: 429 means we could not find out, which the validator
        // renders as Unavailable. Filing it under Unmatched would let an exhausted quota read to the
        // prescriber exactly like a drug that has no label — a quiet "not checked" that hides an outage.
        evidence.Failed.Should().ContainKey(DrugId);
        evidence.Unmatched.Should().BeEmpty();
        evidence.ByDrug.Should().BeEmpty();
    }

    [Fact]
    public async Task A_transport_failure_is_a_failure()
    {
        var source = Source(new ThrowingHandler(new HttpRequestException("dns")));

        var fetched = await source.FetchAsync([new DrugIngredient(DrugId, "warfarin")]);

        Available(fetched).Failed.Should().ContainKey(DrugId);
    }

    [Fact]
    public async Task Refuses_a_label_for_a_different_molecule()
    {
        // The near-miss is the dangerous case, not the miss. Searching "chloride" returns BENZALKONIUM
        // CHLORIDE — a disinfectant — with a 200 and a full interactions section, and taking the first result
        // would present that as this drug's label with total confidence.
        var source = Source(new RecordingHandler(Label("BENZALKONIUM CHLORIDE")));

        var fetched = await source.FetchAsync([new DrugIngredient(DrugId, "chloride")]);

        var evidence = Available(fetched);
        evidence.ByDrug.Should().BeEmpty();
        evidence.Unmatched[DrugId].Should().Contain("none exactly");
    }

    [Fact]
    public async Task Accepts_the_salt_form_of_the_molecule_asked_for()
    {
        var source = Source(new RecordingHandler(Label("ATORVASTATIN CALCIUM")));

        var fetched = await source.FetchAsync([new DrugIngredient(DrugId, "Atorvastatin")]);

        var fact = Available(fetched).ByDrug[DrugId];
        // The matched name is carried, not just the searched one — it is the evidence that the right label
        // came back, and without it a retrieval cannot be audited after the fact.
        fact.MatchedGenericName.Should().Be("ATORVASTATIN CALCIUM");
        fact.InteractionsText.Should().Contain("amiodarone");
    }

    [Fact]
    public async Task Falls_back_to_the_us_spelling_for_an_international_name()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound),   // "paracetamol" — nothing published
            Label("ACETAMINOPHEN"));
        var source = Source(handler);

        var fetched = await source.FetchAsync([new DrugIngredient(DrugId, "paracetamol")]);

        Available(fetched).ByDrug.Should().ContainKey(DrugId);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Caches_a_label_so_revalidation_does_not_spend_the_daily_allowance()
    {
        var handler = new RecordingHandler(Label("WARFARIN"), Label("WARFARIN"));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var source = Source(handler, cache);

        await source.FetchAsync([new DrugIngredient(DrugId, "warfarin")]);
        await source.FetchAsync([new DrugIngredient(DrugId, "warfarin")]);

        // openFDA allows 1,000 requests a day per IP without a key. A prescription revalidated as the doctor
        // types would exhaust a clinic's entire allowance before lunch, and the check would then fail for
        // everyone for the rest of the day.
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Does_not_cache_a_failure_as_an_answer()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            Label("WARFARIN"));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var source = Source(handler, cache);

        var first = await source.FetchAsync([new DrugIngredient(DrugId, "warfarin")]);
        var second = await source.FetchAsync([new DrugIngredient(DrugId, "warfarin")]);

        // A rate limit at 10am must not make the drug uncheckable until 10am tomorrow.
        Available(first).Failed.Should().ContainKey(DrugId);
        Available(second).ByDrug.Should().ContainKey(DrugId);
    }

    [Fact]
    public async Task A_product_with_no_recorded_ingredient_is_reported_as_such()
    {
        var handler = new RecordingHandler();
        var source = Source(handler);

        var fetched = await source.FetchAsync([new DrugIngredient(DrugId, null)]);

        // 2,786 catalogue products are in this state. It costs no request, and the reason is specific enough
        // for someone to act on — it is a data gap, fixable in the catalogue.
        Available(fetched).Unmatched[DrugId].Should().Contain("no active ingredient");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Sends_the_api_key_when_one_is_configured()
    {
        var handler = new RecordingHandler(Label("WARFARIN"));
        var source = Source(handler, key: "test-key");

        await source.FetchAsync([new DrugIngredient(DrugId, "warfarin")]);

        // Without a key the quota is 1,000/day per IP; with one it is 120,000. A single clinic needs the key.
        handler.Requests.Single().Should().Contain("api_key=test-key");
    }

    // ---------------------------------------------------------------- helpers

    private static OpenFdaLabelSource Source(
        HttpMessageHandler handler, IMemoryCache? cache = null, string? key = null)
        => new(
            new StubFactory(handler),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            NullLogger<OpenFdaLabelSource>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(key is null ? [] : new Dictionary<string, string?> { ["OpenFda:ApiKey"] = key })
                .Build());

    private static LabelEvidence Available(Fetched<LabelEvidence> fetched)
    {
        fetched.Should().BeOfType<Fetched<LabelEvidence>.Available>();
        return ((Fetched<LabelEvidence>.Available)fetched).Value;
    }

    private static HttpResponseMessage Label(string genericName) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"results":[{"id":"abc","effective_time":"20260101",
                  "drug_interactions":["Concomitant use of amiodarone increases the risk of bleeding."],
                  "dosage_and_administration":["Individualize dosing."],
                  "dosage_forms_and_strengths":["1mg, 5mg tablets"],
                  "openfda":{"generic_name":["NAME"],"substance_name":["NAME"]}}]}
                """.Replace("NAME", genericName, StringComparison.Ordinal),
                Encoding.UTF8, "application/json"),
        };

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://openfda.test") };
    }

    /// <summary>Answers with each queued response in turn, and records every URL requested.</summary>
    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            var response = _next < responses.Length ? responses[_next] : responses[^1];
            _next++;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(ex);
    }
}
