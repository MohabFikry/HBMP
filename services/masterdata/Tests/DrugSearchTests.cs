using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.MasterData.Api;
using Mersal.MasterData.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.MasterData.Tests;

/// <summary>
/// The prescribing combobox's data source (phase 26.2, doc 43 §6).
/// </summary>
/// <remarks>
/// The requirement these tests exist for is a safety one, not a convenience one: a prescriber searches by
/// whichever name they know, and the option must show the active ingredient before it is chosen. Two trade
/// names holding the same molecule is the commonest prescribing duplication.
/// </remarks>
[Collection("masterdata-db")]
public class DrugSearchTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<JsonElement> Search(HttpClient client, string q, int? pageSize = null)
        => await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/drugs/search?q={Uri.EscapeDataString(q)}" +
                    (pageSize is null ? "" : $"&pageSize={pageSize}"), UriKind.Relative), Web);

    private static List<JsonElement> Items(JsonElement response)
        => [.. response.GetProperty("items").EnumerateArray()];

    [SkippableFact]
    public async Task A_product_is_found_by_its_trade_name_AND_by_its_active_ingredient()
    {
        // The acceptance criterion, in the shape doc 43 states it: "augmentin" and "amoxicillin" must both
        // return the Augmentin rows. The fixture mirrors that — a trade name and an ingredient sharing no
        // substring, so neither result can be an accident of the other matching.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var byTradeName = Items(await Search(client, "zykomentin"));
            var byIngredient = Items(await Search(client, "amoxifixturil"));

            byTradeName.Should().Contain(i => i.GetProperty("drugId").GetGuid() == app.BrandDrugId,
                "a prescriber who knows the brand must find it");
            byIngredient.Should().Contain(i => i.GetProperty("drugId").GetGuid() == app.BrandDrugId,
                "a prescriber who knows the molecule must find the same product");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_option_carries_the_ingredient_price_and_a_REAL_uuid()
    {
        // The regression test for the defect in doc 43 §0: the modal sent `drugId: req.drug.code` — the ATC
        // STRING where the API expects a Guid — so the prescribing path could not work against real data.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var hit = Items(await Search(client, "zykomentin"))
                .Single(i => i.GetProperty("drugId").GetGuid() == app.BrandDrugId);

            hit.GetProperty("drugId").GetGuid().Should().Be(app.BrandDrugId).And.NotBe(Guid.Empty);
            hit.GetProperty("activeIngredient").GetString().Should().Be(app.BrandIngredient,
                "the ingredient is rendered under the trade name and must arrive with the option");
            hit.GetProperty("priceEgp").GetDecimal().Should().Be(210.00m);
            hit.GetProperty("strength").GetString().Should().Be("1g");
            hit.GetProperty("tradeNameAr").GetString().Should().Be("زيكومنتين");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_arabic_query_finds_the_arabic_name()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var hits = Items(await Search(client, "زيكومنتين"));

            hits.Should().Contain(i => i.GetProperty("drugId").GetGuid() == app.BrandDrugId);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Arabic_matching_ignores_tashkeel_and_alef_variants()
    {
        // Short vowels are optional in written Arabic and the four alef forms are not distinguished when
        // typing, so a name stored one way must be found by the other. This is the Arabic half of the same
        // problem unaccent() solves for "Céfalexin" — and it is why search_key normalises both sides.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            await using var db = MasterDataApiFactory.Ctx();

            // "زيكومنتين" queried as "زِيكومنتين" (with a kasra) must still match.
            var withTashkeel = await db.Database
                .SqlQuery<bool>($"SELECT masterdata.search_key('زِيكومنتين') = masterdata.search_key('زيكومنتين') AS \"Value\"")
                .SingleAsync();
            withTashkeel.Should().BeTrue("tashkeel is optional and must not change the match");

            var alefVariants = await db.Database
                .SqlQuery<bool>($"SELECT masterdata.search_key('أسبرين') = masterdata.search_key('اسبرين') AS \"Value\"")
                .SingleAsync();
            alefVariants.Should().BeTrue("users do not distinguish the alef forms when typing");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_trade_name_prefix_outranks_an_ingredient_prefix_which_outranks_a_mere_contains()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // "Testolol 50mg" and "Testolol generic 50mg" both start with the query; "Unrelatide" does not
            // match at all. The brand matches on neither, so the two Testolols must come first.
            var hits = Items(await Search(client, "testolol"));

            hits.Should().NotBeEmpty();
            var ids = hits.Select(i => i.GetProperty("drugId").GetGuid()).ToList();
            ids.Should().Contain(app.DrugAId).And.Contain(app.DrugBId);
            ids.Should().NotContain(app.DrugCId, "Unrelatide matches neither name nor ingredient");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Has_indication_data_distinguishes_no_match_from_nothing_recorded()
    {
        // The distinction 1,019 real products depend on. Without it the UI cannot tell "this diagnosis is
        // not a listed indication" from "nothing is recorded for this drug", and an unchecked drug renders
        // as a checked one — the failure this phase exists to prevent.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var brand = Items(await Search(client, "zykomentin"))
                .Single(i => i.GetProperty("drugId").GetGuid() == app.BrandDrugId);
            var testolol = Items(await Search(client, "testolol"))
                .Single(i => i.GetProperty("drugId").GetGuid() == app.DrugAId);

            brand.GetProperty("hasIndicationData").GetBoolean().Should().BeTrue();
            testolol.GetProperty("hasIndicationData").GetBoolean().Should().BeFalse(
                "no indication rows means the check reports \"not checked\", never \"OK\"");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Superseded_catalogue_rows_are_not_offered_to_a_prescriber()
    {
        // After the workbook load the catalogue holds 8,998 rows from the earlier CSV with no indication
        // data. They stay reachable by id and by code so historical prescriptions resolve, but offering two
        // entries for one product — only one of which can be checked against a diagnosis — would be a safety
        // regression dressed up as completeness.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var ids = Items(await Search(client, "zykomentin"))
                .Select(i => i.GetProperty("drugId").GetGuid()).ToList();

            ids.Should().Contain(app.BrandDrugId);
            ids.Should().NotContain(app.LegacyDrugId, "the legacy row carries no source_row_id");

            // …but it is still retrievable directly, or historical prescriptions would dangle.
            var direct = await client.GetAsync(
                new Uri($"/api/v1/drugs/{app.LegacyDrugCode}", UriKind.Relative));
            direct.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Page_size_is_capped_and_a_short_query_is_refused()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var capped = await Search(client, "test", pageSize: 5000);
            capped.GetProperty("pageSize").GetInt32().Should().Be(DrugSearch.MaxPageSize);
            Items(capped).Count.Should().BeLessThanOrEqualTo(DrugSearch.MaxPageSize);

            // One character matches most of a 22,653-row catalogue; the UI does not send it and the API
            // does not answer it.
            var tooShort = await client.GetAsync(new Uri("/api/v1/drugs/search?q=a", UriKind.Relative));
            tooShort.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var missing = await client.GetAsync(new Uri("/api/v1/drugs/search", UriKind.Relative));
            missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The typeahead reaches its trigram indexes rather than scanning the catalogue.
    /// </summary>
    /// <remarks>
    /// <para>A typeahead that table-scans 31,651 rows per keystroke still returns correct results, so this
    /// fails silently as latency. The index is only used when the query normalises the search term exactly as
    /// the index expression does — assert the plan, not the timing.</para>
    ///
    /// <para><b>This test is about SCALE, and it now establishes the scale it is about.</b> It read the plan
    /// straight out of whatever <c>masterdata.drug</c> happened to hold. Against a loaded catalogue the
    /// planner picks the index and the test passes; against an empty one it picks a sequential scan and is
    /// RIGHT to — at a few hundred rows a seq scan genuinely is cheaper, and the assertion is not merely
    /// failing but meaningless. CI runs no catalogue load, so it failed there for months
    /// (<c>cost=0.00..94.43</c>) while passing on every developer's machine.</para>
    ///
    /// <para>The crossover on this schema sits between 300 and 500 rows, measured. Seeding to
    /// <see cref="CatalogueScale"/> clears it by roughly four times, which is the margin that keeps this
    /// about the index rather than about the planner's arithmetic. When the real catalogue IS loaded the seed
    /// is skipped entirely and the test reads exactly what it always did.</para>
    /// </remarks>
    [SkippableFact]
    public async Task The_search_uses_its_trigram_indexes_rather_than_scanning_the_catalogue()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var db = MasterDataApiFactory.Ctx();
        var release = "scale-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await EnsureCatalogueScaleAsync(db, release);

            var plan = string.Join('\n', await db.Database.SqlQuery<string>($"""
                EXPLAIN SELECT d.drug_id FROM masterdata.drug d
                WHERE d.source_row_id IS NOT NULL
                  AND (   masterdata.search_key(d.name)            LIKE '%zykomentin%'
                       OR masterdata.search_key(d.scientific_name) LIKE '%zykomentin%'
                       OR masterdata.search_key(d.name_ar)         LIKE '%zykomentin%')
                """).ToListAsync());

            plan.Should().Contain("ix_drug_search_name", "the trigram index on the trade name must be used");
            plan.Should().NotContain("Seq Scan on drug",
                "a sequential scan per keystroke is the failure this index exists to prevent");
        }
        finally
        {
            // Remove only what this run added, and re-ANALYZE: leaving stale statistics behind would hand the
            // next test in this collection a planner that believes in rows that are gone.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM masterdata.drug WHERE source_release = {0}; ANALYZE masterdata.drug;", release);
        }
    }

    /// <summary>The row count at which the trigram index is unambiguously the cheaper plan — see the remarks
    /// on the test above for how it was chosen.</summary>
    private const int CatalogueScale = 2_000;

    /// <summary>
    /// Bring <c>masterdata.drug</c> up to <see cref="CatalogueScale"/> rows, and no further.
    /// </summary>
    /// <remarks>
    /// A no-op wherever the catalogue is loaded. The synthetic rows carry a run-scoped
    /// <c>source_release</c> so the caller can remove exactly them, and a <c>source_row_id</c> because the
    /// query under test filters on it — rows the planner would discard are not the rows it must count.
    /// <c>ANALYZE</c> is the point of the whole exercise: without fresh statistics the planner reasons about
    /// the table as it was before the insert.
    /// </remarks>
    private static async Task EnsureCatalogueScaleAsync(MasterDataDbContext db, string release)
    {
        var have = await db.Drugs.CountAsync();
        if (have >= CatalogueScale) return;

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO masterdata.drug (drug_id, drug_code, name, scientific_name, name_ar, source_release, source_row_id)
            SELECT gen_random_uuid(),
                   {0} || '-' || g,
                   'Scale fixture ' || g || ' 500mg',
                   'scalefixturil ' || g,
                   'عينة ' || g,
                   {0},
                   {0} || '-row-' || g
            FROM generate_series(1, {1}) g;
            ANALYZE masterdata.drug;
            """,
            release, CatalogueScale - have);
    }

    // ---- 29.7 (design 45 §7) — the price/availability facts the combobox renders ------------------------
    //
    // `LowestPrice.Compute` and `DbUpsert.RecomputeLowestPriceAsync` were correct and tested, and the
    // columns were populated on every load — but THIS query never selected them, so the value stopped at
    // the database. The chip could not render against a real backend however right the computation was.
    // A unit test of the computation cannot see that; only a test of the endpoint the UI actually calls can.

    [SkippableFact]
    public async Task An_option_carries_the_lowest_price_verdict_and_the_per_unit_price_it_rests_on()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            await using (var db = MasterDataApiFactory.Ctx())
            {
                var d = await db.Drugs.FirstAsync(x => x.DrugId == app.BrandDrugId);
                (d.IsLowestPrice, d.PricePerUnit) = (true, 10.50m);
                await db.SaveChangesAsync();
            }
            using var client = app.ClinicalClient();

            var hit = Items(await Search(client, "zykomentin")).Single(i => i.GetProperty("drugId").GetGuid() == app.BrandDrugId);

            hit.GetProperty("isLowestPrice").GetBoolean().Should().BeTrue();
            hit.GetProperty("pricePerUnit").GetDecimal().Should().Be(10.50m,
                "the per-unit figure is the comparison basis — price ÷ pack size, never the pack price");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_option_carries_its_availability_and_defaults_to_Unknown()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            await using (var db = MasterDataApiFactory.Ctx())
            {
                (await db.Drugs.FirstAsync(x => x.DrugId == app.BrandDrugId)).Availability = "Unavailable";
                await db.SaveChangesAsync();
            }
            using var client = app.ClinicalClient();

            var items = Items(await Search(client, "zykomentin"));
            items.Single(i => i.GetProperty("drugId").GetGuid() == app.BrandDrugId)
                .GetProperty("availability").GetString().Should().Be("Unavailable");

            // An untouched row carries the catalogue default, which renders NOTHING. A boolean here would
            // have shown the whole catalogue as out of stock on day one.
            Items(await Search(client, "fixturevax")).Single(i => i.GetProperty("drugId").GetGuid() == app.VaccineDrugId)
                .GetProperty("availability").GetString().Should().Be("Unknown");
        }
        finally { await app.CleanupAsync(); }
    }
}
