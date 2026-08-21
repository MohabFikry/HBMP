using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Mersal.MasterData.Domain;
using Mersal.MasterData.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mersal.MasterData.Tests;

/// <summary>
/// Phase 24 Gate 3 — the reference catalogue, read through its endpoints against a real database.
///
/// <para>masterdata-service had two test files: a mapper unit test and an authorization suite that inspects
/// route metadata without a database. Nothing had ever executed a query. That matters more than the module's
/// size suggests, because these are the FAIL-CLOSED contracts the rest of the platform depends on: orders
/// refuses a line whose code <c>/icd-codes/{code}/exists</c> does not confirm, pharmacy blocks a dispense on
/// what <c>/drug-interactions/check-by-ids</c> returns, and orders pins a result's sensitivity from
/// <c>/examination-types/{id}</c>. An "exists" endpoint that answered <c>true</c> for everything would have
/// failed no test here and quietly disabled a validation gate in three other services.</para>
///
/// <para>The catalogue is tenant-FREE by design (see MasterDataAuthzTests), so these tests scope themselves
/// by a unique source release and clean up on that instead.</para>
/// </summary>
[Collection("masterdata-db")]
public class MasterDataEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- D5: "is this a medicine?" -----------------------------------------------------------------------
    //
    // inventory-service refuses a clinic-stock catalogue item on what this endpoint answers (ADR-0029 D5).
    // It is therefore the same shape of contract as the /exists routes above and fails the same way: an
    // endpoint that answered `matched: false` for everything would break no test in inventory — its guard
    // would simply stop guarding — so the negative and the containment case are both asserted HERE, against
    // real rows, rather than against a fake on the calling side.

    [SkippableFact]
    public async Task An_authenticated_caller_without_the_scope_is_refused()
    {
        // 26.1 turned a bare RequireAuthorization() into a scope gate, and the whole suite went 403 in one
        // step — which is the failure mode worth pinning. Authenticated is no longer sufficient, and if that
        // ever silently reverts, every token on the platform regains the catalogue.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClientWithScopes();   // authenticated, no scopes

            var response = await client.GetAsync(new Uri("/api/v1/icd-codes?pageSize=1", UriKind.Relative));

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden,
                "a token without masterdata:read reaches no part of the catalogue");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_drug_code_is_classified_as_a_medicine_and_an_unrelated_consumable_is_not()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var byCode = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/drugs/classify?code={app.DrugACode}", UriKind.Relative), Web);
            byCode.GetProperty("matched").GetBoolean().Should().BeTrue();
            byCode.GetProperty("drugCode").GetString().Should().Be(app.DrugACode);
            byCode.GetProperty("isVaccine").GetBoolean().Should().BeFalse("Testolol is not in ATC J07");

            // The negation, and the one that decides whether the guard is usable: gauze must go in. A
            // classify that matched everything would refuse the entire clinic catalogue, and the guard would
            // be switched off within a day.
            var gauze = await client.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/drugs/classify?code=GZ-1010&name=Gauze%20swab%2010x10", UriKind.Relative), Web);
            gauze.GetProperty("matched").GetBoolean().Should().BeFalse();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_vaccine_is_matched_by_a_name_that_MERELY_CONTAINS_it_and_is_flagged_as_a_vaccine()
    {
        // The real mistake is never typed exactly. Someone catalogues "Fixturevax Vaccine 20mcg/ml vial" and
        // an equality match lets it straight through — which is why the containment arm exists and why it is
        // asserted separately from the exact-code arm above.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var typed = Uri.EscapeDataString(app.VaccineDrugName + " 20mcg/ml vial");
            var hit = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/drugs/classify?code=VAX-99&name={typed}", UriKind.Relative), Web);

            hit.GetProperty("matched").GetBoolean().Should().BeTrue("the master's name is contained in what was typed");
            hit.GetProperty("drugCode").GetString().Should().Be(app.VaccineDrugCode);
            hit.GetProperty("isVaccine").GetBoolean().Should().BeTrue("ATC J07 is the vaccines group");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Classify_needs_something_to_go_on()
    {
        // An empty query must be a 400, not a cheerful `matched: false`. The caller fails CLOSED on anything
        // it cannot interpret, and a 200 saying "not a medicine" is the one answer that would let a
        // malformed call through as an approval.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            using var client = app.ClinicalClient();
            var res = await client.GetAsync(new Uri("/api/v1/drugs/classify", UriKind.Relative));
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_icd_code_that_exists_is_confirmed_and_one_that_does_not_is_denied()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var known = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/icd-codes/{app.IcdCode}/exists", UriKind.Relative), Web);
            known.GetProperty("exists").GetBoolean().Should().BeTrue();

            // The normaliser runs on the way in, so a caller that types the code in lower case gets an
            // answer about the same code rather than a false "no such code". It upper-cases and trims and
            // does NOT insert the dot — the stored form is dotted and the caller must send it that way.
            var lowercased = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/icd-codes/{app.IcdCode.ToLowerInvariant()}/exists", UriKind.Relative), Web);
            lowercased.GetProperty("code").GetString().Should().Be(app.IcdCode);
            lowercased.GetProperty("exists").GetBoolean().Should().BeTrue();

            var absent = await client.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/icd-codes/Z99.999/exists", UriKind.Relative), Web);
            absent.GetProperty("exists").GetBoolean().Should().BeFalse(
                "orders and emr treat 'not confirmed' as 'refuse the line' — a permissive answer here " +
                "silently disables their validation");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_drug_resolves_by_code_and_by_id_and_an_unknown_one_is_a_404()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var resolved = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/drugs/resolve?code={app.DrugACode}", UriKind.Relative), Web);
            resolved.GetProperty("name").GetString().Should().Be("Testolol 50mg");
            resolved.GetProperty("atcCode").GetString().Should().Be(app.AtcCode);

            var byId = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/drugs/by-id/{app.DrugAId}/exists", UriKind.Relative), Web);
            byId.GetProperty("exists").GetBoolean().Should().BeTrue();

            (await client.GetAsync(new Uri("/api/v1/drugs/resolve?code=NO-SUCH-DRUG", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            var missing = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/drugs/by-id/{Guid.NewGuid()}/exists", UriKind.Relative), Web);
            missing.GetProperty("exists").GetBoolean().Should().BeFalse();
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The active ingredients the prescribing path looks manufacturer labels up by.
    /// </summary>
    /// <remarks>
    /// Every id asked about is answered for, including those with no ingredient recorded — 2,786 real
    /// products are in that state. Omitting them would leave the caller unable to tell "this product has no
    /// recorded ingredient" from "I forgot to ask about it", and the live label check reports the first of
    /// those to the prescriber as a specific, fixable data gap.
    /// </remarks>
    [SkippableFact]
    public async Task Ingredients_are_returned_for_every_id_asked_about_including_the_blanks()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();
            var unknown = Guid.NewGuid();

            var response = await client.PostAsJsonAsync(
                new Uri("/api/v1/drugs/ingredients/by-ids", UriKind.Relative),
                new { drugIds = new[] { app.DrugAId, app.DrugBId, unknown } });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
            var items = body.GetProperty("items").EnumerateArray()
                .ToDictionary(i => i.GetProperty("drugId").GetGuid(), i => i.GetProperty("scientificName"));

            items.Should().HaveCount(3);
            items[app.DrugAId].GetString().Should().Be("testolol");
            items[app.DrugBId].ValueKind.Should().Be(JsonValueKind.Null, "this fixture has no ingredient");
            items[unknown].ValueKind.Should().Be(JsonValueKind.Null, "an unknown id is answered, not dropped");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The formulary substitution set pharmacy consults: the other drugs in the SAME ATC-5 class,
    /// never the drug itself, and never one from a different class.</summary>
    [SkippableFact]
    public async Task Alternatives_are_the_same_atc_class_excluding_the_drug_itself()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var alts = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/drugs/by-id/{app.DrugAId}/alternatives", UriKind.Relative), Web);
            var ids = alts.GetProperty("alternatives").EnumerateArray().Select(e => e.GetGuid()).ToList();

            ids.Should().Contain(app.DrugBId, "B shares A's ATC-5 class");
            ids.Should().NotContain(app.DrugAId, "a drug is not its own alternative");
            ids.Should().NotContain(app.DrugCId, "C is in a different ATC class — substituting it would be a " +
                                                 "different therapeutic substance");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The interaction screen pharmacy blocks a dispense on. Both directions of the pair are
    /// checked, because the stored pair is order-insensitive and a screen that only matched (A,B) would let
    /// the same two drugs through when the prescription happened to list them the other way round.</summary>
    [SkippableFact]
    public async Task The_interaction_screen_reports_the_highest_severity_in_either_order()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var forward = await Post(client, "/api/v1/drug-interactions/check-by-ids",
                new { drugIds = new[] { app.DrugAId, app.DrugBId } });
            (await forward.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("highestSeverity").GetString().Should().Be(nameof(InteractionSeverity.Major));

            var reversed = await Post(client, "/api/v1/drug-interactions/check-by-ids",
                new { drugIds = new[] { app.DrugBId, app.DrugAId } });
            (await reversed.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("highestSeverity").GetString().Should().Be(nameof(InteractionSeverity.Major));

            var byCode = await Post(client, "/api/v1/drug-interactions/check",
                new { drugCodes = new[] { app.DrugACode, app.DrugBCode } });
            (await byCode.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("highestSeverity").GetString().Should().Be(nameof(InteractionSeverity.Major));

            // A drug on its own interacts with nothing, and the endpoint says so rather than omitting the field.
            var alone = await Post(client, "/api/v1/drug-interactions/check-by-ids", new { drugIds = new[] { app.DrugAId } });
            (await alone.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("highestSeverity").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The sensitivity classification orders PINS onto a result row (design 37 §5). This is the source of a
    /// denormalized field that gates who may read a result for the rest of that record's life, so the value
    /// this endpoint returns has consequences long after the call.
    /// </summary>
    [SkippableFact]
    public async Task An_examination_type_carries_its_sensitivity_and_special_category()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var one = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/examination-types/{app.SensitiveExamId}", UriKind.Relative), Web);
            one.GetProperty("sensitivityLevel").GetString().Should().Be(nameof(SensitivityLevel.HighlySensitive));
            one.GetProperty("sensitiveCategory").GetString().Should().Be(nameof(SensitiveCategory.MentalHealth));
            one.GetProperty("nameAr").GetString().Should().Be("تقييم الصحة النفسية");

            var filtered = await client.GetFromJsonAsync<List<JsonElement>>(
                new Uri($"/api/v1/examination-types?sensitivity={nameof(SensitivityLevel.HighlySensitive)}", UriKind.Relative), Web);
            filtered.Should().NotBeNull();
            var ids = filtered!.Select(e => e.GetProperty("examinationTypeId").GetGuid()).ToList();
            ids.Should().Contain(app.SensitiveExamId);
            ids.Should().NotContain(app.StandardExamId);

            // A retired type is not resolvable: orders would otherwise pin a classification from a row
            // nobody maintains any more.
            (await client.GetAsync(new Uri($"/api/v1/examination-types/{app.RetiredExamId}", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Typeahead_search_finds_by_code_prefix_and_by_name_and_is_capped()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var byCode = await client.GetFromJsonAsync<List<JsonElement>>(
                new Uri($"/api/v1/search?domain=icd&q={app.IcdCode}", UriKind.Relative), Web);
            byCode!.Select(e => e.GetProperty("code").GetString()).Should().Contain(app.IcdCode);

            // The SAME code typed the way people type it. `StartsWith` is `LIKE 'q%'` and case-sensitive in
            // Postgres, so this used to return nothing while the title half of the very same query was already
            // case-insensitive — a search box offering "by code or condition" that honoured the promise for
            // one of them. The fixture code is upper-case hex by construction, so lower-casing it is a
            // genuinely different string rather than a test that would pass either way.
            var byLowerCode = await client.GetFromJsonAsync<List<JsonElement>>(
                new Uri($"/api/v1/search?domain=icd&q={app.IcdCode.ToLowerInvariant()}", UriKind.Relative), Web);
            byLowerCode!.Select(e => e.GetProperty("code").GetString()).Should().Contain(app.IcdCode);

            // And the condition text, in a case the row is not stored in — the half that already worked, now
            // pinned so a future rewrite of this expression cannot trade one for the other.
            var byTitle = await client.GetFromJsonAsync<List<JsonElement>>(
                new Uri("/api/v1/search?domain=icd&q=REFERENCE%20FIXTURE", UriKind.Relative), Web);
            byTitle!.Select(e => e.GetProperty("code").GetString()).Should().Contain(app.IcdCode);

            // A CHAPTER OR BLOCK IS NOT A DIAGNOSIS. Both fixtures answer "Z00" and both answer "REFERENCE
            // FIXTURE", so either query returns the pair unless the endpoint excludes grouping rows — which
            // is what makes this an assertion about the filter rather than about the fixtures. The doctor's
            // diagnosis field is fed from here, and a range heading staged as a visit's PRIMARY diagnosis is
            // the code the authorization, the claim and the formulary check would then key on.
            byTitle!.Select(e => e.GetProperty("code").GetString()).Should().NotContain(app.IcdBlockCode);

            // The same exclusion reached by CODE rather than by text. "Z00" is a prefix of both fixtures
            // ("Z00.XXXX" and "Z00-XXXX"), so the leaf arriving without the heading is the filter working and
            // not the query simply missing it — searching the leaf's full code could never have returned the
            // heading and would have asserted nothing.
            var byPrefix = await client.GetFromJsonAsync<List<JsonElement>>(
                new Uri("/api/v1/search?domain=icd&q=Z00", UriKind.Relative), Web);
            var prefixCodes = byPrefix!.Select(e => e.GetProperty("code").GetString()).ToList();
            prefixCodes.Should().NotContain(app.IcdBlockCode);

            // Two columns, and no third. The list IS the code and what it means; `chapter` rode along on
            // every keystroke to be dropped on arrival by the only caller there is.
            byTitle!.Should().NotBeEmpty()
                .And.AllSatisfy(e => e.EnumerateObject().Select(p => p.Name)
                    .Should().BeEquivalentTo("code", "title"));

            // The same shape from the catalogue listing, whose items used to be whole rows — chapter,
            // is_billable, icd11Map, source_release and all.
            var listed = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/icd-codes?q={app.IcdCode}", UriKind.Relative), Web);
            listed.GetProperty("items").EnumerateArray().Should().NotBeEmpty()
                .And.AllSatisfy(e => e.EnumerateObject().Select(p => p.Name)
                    .Should().BeEquivalentTo("code", "title"));

            // The listing is the CATALOGUE, so it still holds its own hierarchy — the filter belongs to the
            // typeahead, which answers "which diagnosis do you mean". Asked plainly, the heading is there.
            var headings = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/icd-codes?q={app.IcdBlockCode}&billable=false", UriKind.Relative), Web);
            headings.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("code").GetString()).Should().Contain(app.IcdBlockCode);

            var byName = await client.GetFromJsonAsync<List<JsonElement>>(
                new Uri("/api/v1/search?domain=drug&q=Testolol", UriKind.Relative), Web);
            byName!.Select(e => e.GetProperty("drugCode").GetString()).Should().Contain(app.DrugACode);

            // An unknown domain returns nothing rather than everything — the failure mode of a `_ =>` arm
            // that fell through to an unfiltered query would be a full catalogue dump on a typo.
            var unknownDomain = await client.GetFromJsonAsync<List<JsonElement>>(
                new Uri("/api/v1/search?domain=nonsense&q=a", UriKind.Relative), Web);
            unknownDomain.Should().BeEmpty();

            (await client.GetAsync(new Uri("/api/v1/search?domain=icd&q=", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The CPT sections that separate the Labs tab from the Imaging tab.
    /// </summary>
    /// <remarks>
    /// Asserted against the REAL catalogue rather than fixtures, and deliberately. The whole claim being
    /// made is that a set of numeric ranges partitions the published CPT book correctly — a claim about the
    /// 10,810 rows the clinic actually orders from, which seeded rows in convenient ranges could only ever
    /// restate. The codes named below are stable published CPT identities (80048 basic metabolic panel,
    /// 88305 surgical pathology, 71046 chest radiograph), not fixtures.
    /// </remarks>
    [SkippableFact]
    public async Task Cpt_sections_separate_imaging_from_laboratory_and_pathology()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            using var client = app.ClinicalClient();

            static async Task<List<JsonElement>> Items(HttpClient c, string url) =>
                (await c.GetFromJsonAsync<JsonElement>(new Uri(url, UriKind.Relative), Web))
                    .GetProperty("items").EnumerateArray().ToList();
            static List<string?> Codes(List<JsonElement> items) =>
                items.Select(e => e.GetProperty("code").GetString()).ToList();

            // The Imaging tab. Every code in the section is 70010–79999 and nothing else is, so the property
            // holds over the whole page rather than for a row someone remembered to check.
            var imaging = await Items(client, "/api/v1/cpt-codes?section=Imaging&pageSize=200");
            Codes(imaging).Should().NotBeEmpty().And.AllSatisfy(c => c.Should().MatchRegex(@"^7\d{4}$"));

            // The Labs tab asks for TWO sections, which is the reason the parameter takes a list: a specimen
            // sent to a pathologist and a sample run on an analyser are ordered from the same tab and are not
            // the same section.
            var labs = await Items(client, "/api/v1/cpt-codes?section=Laboratory,Pathology&q=88305");
            Codes(labs).Should().Contain("88305");
            Codes(await Items(client, "/api/v1/cpt-codes?section=Laboratory,Pathology&q=80048")).Should().Contain("80048");

            // And the split itself. Each half must EXCLUDE the other's flagship code, or "Laboratory,Pathology"
            // is two names for one filter and the categorization is decorative.
            Codes(await Items(client, "/api/v1/cpt-codes?section=Laboratory&q=88305")).Should().BeEmpty();
            Codes(await Items(client, "/api/v1/cpt-codes?section=Pathology&q=80048")).Should().BeEmpty();

            // Cross-tab: the Imaging tab cannot offer a blood test, and the Labs tab cannot offer a chest
            // film. This is the mistake the sections exist to make impossible to make.
            Codes(await Items(client, "/api/v1/cpt-codes?section=Imaging&q=80048")).Should().BeEmpty();
            Codes(await Items(client, "/api/v1/cpt-codes?section=Laboratory,Pathology&q=71046")).Should().BeEmpty();
            Codes(await Items(client, "/api/v1/cpt-codes?section=Imaging&q=71046")).Should().Contain("71046");

            // A section this build has not heard of must not read as "match nothing" — a caller running ahead
            // of a deployment would show the doctor an empty catalogue, which looks exactly like a clinic
            // with no chest x-ray in it.
            Codes(await Items(client, "/api/v1/cpt-codes?section=Nonsense&q=71046")).Should().Contain("71046");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Search over the CPT catalogue: case-insensitive on both fields, two columns out, and the
    /// kind of match that leads decided by what the doctor typed.</summary>
    [SkippableFact]
    public async Task Cpt_search_is_case_insensitive_two_columns_and_leads_with_the_kind_of_match_typed()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            using var client = app.ClinicalClient();

            static async Task<List<JsonElement>> Items(HttpClient c, string url) =>
                (await c.GetFromJsonAsync<JsonElement>(new Uri(url, UriKind.Relative), Web))
                    .GetProperty("items").EnumerateArray().ToList();

            // Case-insensitive on the DESCRIPTION — it already was, and stays pinned.
            var upper = await Items(client, "/api/v1/cpt-codes?section=Imaging&q=CHEST&pageSize=200");
            var lower = await Items(client, "/api/v1/cpt-codes?section=Imaging&q=chest&pageSize=200");
            upper.Should().NotBeEmpty();
            upper.Select(e => e.GetProperty("code").GetString())
                .Should().Equal(lower.Select(e => e.GetProperty("code").GetString()));

            // Case-insensitive on the CODE, which it was not. CPT codes are not all digits: Category II, III,
            // PLA and MAAA codes are four digits and a letter, so "0001u" and "0001U" are the same request
            // and only one of them used to find the row.
            var lowerSuffix = await Items(client, "/api/v1/cpt-codes?q=0001u");
            lowerSuffix.Select(e => e.GetProperty("code").GetString()).Should().Contain("0001U");

            // Two columns and no third — `category` and `sourceRelease` used to ride out on every keystroke
            // of a typeahead that displays neither.
            lowerSuffix.Should().AllSatisfy(e => e.EnumerateObject().Select(pr => pr.Name)
                .Should().BeEquivalentTo("code", "description"));

            // ------------------------------------------------------------------------------------------
            // A DIGIT LEADS WITH THE CODE
            // ------------------------------------------------------------------------------------------
            // 82947 is "Glucose; quantitative, blood" — and NINE panel descriptions cite it (80047 basic
            // metabolic, 80048, 80053 comprehensive metabolic, and so on), every one of them a lower code.
            // That is what makes this the probe: sorted by code alone, a doctor typing the glucose code gets
            // nine panels above the test they asked for, and the code they typed is the tenth row. The two
            // orderings genuinely disagree here, which most numeric queries do not — "9921" looks like a fair
            // test and is not, because its text matches (99354, 99417) sort last under either rule.
            var digits = await Items(client, "/api/v1/cpt-codes?q=82947&pageSize=200");
            var digitCodes = digits.Select(e => e.GetProperty("code").GetString()!).ToList();
            digitCodes.Should().Contain("82947");
            digitCodes.Should().Contain(c => !c.StartsWith("82947", StringComparison.Ordinal));
            var lastCodeMatch = digitCodes.FindLastIndex(c => c.StartsWith("82947", StringComparison.Ordinal));
            var firstTextOnly = digitCodes.FindIndex(c => !c.StartsWith("82947", StringComparison.Ordinal));
            lastCodeMatch.Should().BeLessThan(firstTextOnly);

            // ------------------------------------------------------------------------------------------
            // LETTERS LEAD WITH THE DESCRIPTION
            // ------------------------------------------------------------------------------------------
            // Which is the whole result set here, and that is the point rather than a weak assertion: no CPT
            // code BEGINS with a letter, so a worded query can only ever match descriptions. The rule is
            // stated in the endpoint so the numeric case has an explicit counterpart, and pinned here so a
            // later change to match codes by containment cannot silently reorder a worded search.
            var worded = await Items(client, "/api/v1/cpt-codes?q=chest&pageSize=200");
            worded.Should().NotBeEmpty().And.AllSatisfy(e =>
                e.GetProperty("description").GetString()!.ToLowerInvariant().Should().Contain("chest"));
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Listing_paginates_and_filters_rather_than_returning_the_catalogue()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var page = await client.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/icd-codes?pageSize=1", UriKind.Relative), Web);
            page.GetProperty("pageSize").GetInt32().Should().Be(1);
            page.GetProperty("items").GetArrayLength().Should().Be(1);

            // pageSize is CLAMPED, not trusted: an unbounded page is how a reference read becomes an export.
            var oversized = await client.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/icd-codes?pageSize=100000", UriKind.Relative), Web);
            oversized.GetProperty("pageSize").GetInt32().Should().Be(200);

            var byAtc = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/drugs?atcCode={app.AtcCode}", UriKind.Relative), Web);
            var codes = byAtc.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("drugCode").GetString()).ToList();
            codes.Should().Contain(app.DrugACode).And.Contain(app.DrugBCode).And.NotContain(app.DrugCCode);
        }
        finally { await app.CleanupAsync(); }
    }

    /*
     * ---------------------------------------------------------------------------------------------------
     * THE ALLERGY MATCHER, AGAINST THE REAL SEEDED ALLERGENS (phase 28.1.1, doc 44 §1.1).
     *
     * This endpoint had NO test at all, which is how it shipped structurally incapable of ever raising a
     * conflict: it built the drug's ATC ancestor chain (J, J01, J01C) and asked whether a recorded allergen
     * CODE (ALG-PENICILLIN) appeared in it. Two disjoint code spaces; a constant false; and the prescribing
     * engine rendering that as "no conflict with the 3 recorded allergies".
     *
     * The fixture allergens are deliberately the REAL ones from migration 0002 rather than rows this test
     * inserts. A matcher tested only against fixtures it also authored is a matcher tested against its own
     * assumptions — and the assumption that broke was about the shape of the shipped seed data.
     * ---------------------------------------------------------------------------------------------------
     */
    [SkippableFact]
    public async Task A_PENICILLIN_ALLERGIC_BENEFICIARY_PRESCRIBED_AMOXICILLIN_IS_FLAGGED()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            await using var db = MasterDataApiFactory.Ctx();
            var penicillin = await db.Allergens.AsNoTracking().FirstOrDefaultAsync(a => a.Code == "ALG-PENICILLIN");
            var peanut = await db.Allergens.AsNoTracking().FirstOrDefaultAsync(a => a.Code == "ALG-PEANUT");
            Skip.If(penicillin is null || peanut is null, "seeded allergens absent — migration 0002 has not run.");
            Skip.If(!await db.Set<Ingredient>().AnyAsync(i => i.IngredientKey == "amoxicillin"),
                "ingredient seed absent — migration 0009 has not run.");

            using var response = await Post(client, "/api/v1/allergies/check-by-ids",
                new { drugId = app.AmoxicillinDrugId, allergenIds = new[] { penicillin!.AllergenId, peanut!.AllergenId } });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Web);

            // THE ASSERTION THAT DID NOT EXIST. This was false for every patient and every medicine.
            body.GetProperty("conflict").GetBoolean().Should().BeTrue(
                "a penicillin-allergic beneficiary prescribed amoxicillin must be warned");
            body.GetProperty("matchKind").GetString().Should().Be("ExactIngredient");
            body.GetProperty("matchedOn").GetString().Should().Contain("amoxicillin");

            // The drug allergen was compared; the food one is not a question about a medicine, so it is
            // neither screened nor reported as a gap in the catalogue.
            body.GetProperty("screenedAllergenCount").GetInt32().Should().Be(1);
            body.GetProperty("unmappedAllergens").EnumerateArray().Should().BeEmpty();
            body.GetProperty("drugResolvable").GetBoolean().Should().BeTrue();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task No_seeded_drug_allergen_silently_passes()
    {
        // The sweep doc 44 §11 asks for. Every allergen the platform ships is either mapped to something a
        // medicine can be compared against, or explicitly not a medicine-related allergen. An allergen that
        // is neither would produce a confident "no conflict" against a medicine nobody checked it against —
        // which is the exact shape of the defect this phase exists to remove, one allergen at a time.
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var db = MasterDataApiFactory.Ctx();

        var allergens = await db.Allergens.AsNoTracking().ToListAsync();
        Skip.If(allergens.Count == 0, "seeded allergens absent — migration 0002 has not run.");

        var exact = await db.Set<AllergenIngredient>().AsNoTracking().ToListAsync();
        var crossReactivity = await db.Set<AllergenCrossReactivity>().AsNoTracking().ToListAsync();

        var silent = allergens
            .Where(a => a.IsDrugMappable && a.Category == AllergenCategory.Drug)
            .Where(a => (a.AtcScopes ?? []).Length == 0
                        && !exact.Any(x => x.AllergenId == a.AllergenId)
                        && !crossReactivity.Any(x => x.AllergenId == a.AllergenId))
            .Select(a => a.Code)
            .ToList();

        silent.Should().BeEmpty(
            "a drug allergen with no molecule, no ATC scope and no cross-reactivity group cannot be compared "
            + "with any medicine, so every prescription would report it as unchecked for ever");

        // And the governance half: a mapping that decides whether a prescriber is warned has a named
        // reviewer. Migration 0009 enforces it with a CHECK; this asserts the shipped rows satisfy it.
        allergens.Where(a => (a.AtcScopes ?? []).Length > 0)
            .Should().AllSatisfy(a => a.MappingReviewedBy.Should().NotBeNullOrWhiteSpace());
        exact.Should().AllSatisfy(x => x.ReviewedBy.Should().NotBeNullOrWhiteSpace());
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, object body)
    {
        using var content = JsonContent.Create(body, options: Web);
        return await client.PostAsync(new Uri(url, UriKind.Relative), content);
    }
}

/// <summary>Serializes the masterdata DB tests — they seed into and delete from the shared catalogue.</summary>
[Xunit.CollectionDefinition("masterdata-db", DisableParallelization = true)]
public sealed class MasterDataDbTestGroup;

/// <summary>Hosts the real masterdata endpoints against the env-gated Postgres, and owns the reference rows
/// the tests read. Every seeded row carries this factory's own source release, which is what cleanup keys
/// on — the catalogue has no tenant column to scope by.</summary>
public sealed class MasterDataApiFactory : WebApplicationFactory<Program>
{
    public static readonly string? Db = Environment.GetEnvironmentVariable("MASTERDATA_TEST_DB");

    public string Release { get; } = "test-" + Guid.NewGuid().ToString("N")[..10];
    private string Suffix => Release[^10..];

    /// <summary>Four upper-case hex characters. Every natural key below is built from it, because these
    /// tables hold REAL reference data: a fixture on a plausible code collides with the loaded catalogue
    /// (99001 is a real CPT code, and that is how this suite first failed).</summary>
    private string Token => Suffix[..4].ToUpperInvariant();

    public string IcdCode => $"Z00.{Token}";

    /// <summary>A GROUPING row — the shape the source sheet uses for a chapter or block ("A00-A09"), which the
    /// loader marks non-billable. Deliberately sharing the leaf's <c>Z00</c> prefix so one search returns both
    /// unless the typeahead filters, which is the only way to prove that it does.</summary>
    public string IcdBlockCode => $"Z00-{Token}";
    public string CptCode => $"Z{Token}";
    /// <summary>Seven characters, so AtcLevel reads it as a level-5 substance. No real ATC group is Z.</summary>
    public string AtcCode => $"Z{Token}A01";
    public string OtherAtcCode => $"Z{Token}B01";
    public string DrugACode => $"TSTA-{Suffix}";
    public string DrugBCode => $"TSTB-{Suffix}";
    public string DrugCCode => $"TSTC-{Suffix}";

    /// <summary>A vaccine fixture, for the D5 classify contract. J07 is the real ATC group for vaccines and
    /// <c>/drugs/classify</c> reports <c>isVaccine</c> off that prefix, so the fixture has to start with it
    /// while the rest keeps it unique against the loaded catalogue.</summary>
    public string VaccineAtcCode => $"J07{Token}01";
    public string VaccineDrugCode => $"TSTV-{Suffix}";
    public string VaccineDrugName => "Fixturevax Vaccine";
    /// <summary>
    /// A real penicillin-class product, for the case doc 44 §1.1 says silently passed: a penicillin-allergic
    /// beneficiary prescribed amoxicillin.
    /// </summary>
    /// <remarks>
    /// Its ATC is the REAL J01CA04 rather than the Z-namespaced fixture codes the other drugs use, because
    /// the whole point is that it must fall inside the J01C scope the seeded ALG-PENICILLIN mapping carries.
    /// A fixture code would test the matcher against a scope invented for the test, which is how the
    /// original defect survived: everything it was checked against had been built to agree with it.
    /// </remarks>
    /// <summary>Fixture molecules. Namespaced by the run token so they cannot collide with the curated seed.</summary>
    public string FixtureIngredientA => $"testolol-{Suffix}";
    public string FixtureIngredientB => $"unrelatide-{Suffix}";

    public string AmoxicillinAtcCode => "J01CA04";
    public string AmoxicillinDrugCode => $"TSTAMX-{Suffix}";
    public Guid AmoxicillinDrugId { get; } = Guid.NewGuid();

    public Guid DrugAId { get; } = Guid.NewGuid();
    public Guid DrugBId { get; } = Guid.NewGuid();
    public Guid DrugCId { get; } = Guid.NewGuid();
    public Guid VaccineDrugId { get; } = Guid.NewGuid();

    // ---- 26.2 typeahead fixtures ----------------------------------------------------------------------
    // A brand whose trade name and active ingredient share no substring, mirroring Augmentin /
    // amoxicillin+clavulanic acid. Both must reach it, which is the whole point of the one-field search:
    // two trade names holding the same molecule is the commonest prescribing duplication.
    public string BrandDrugCode => $"TSTBR-{Suffix}";
    public Guid BrandDrugId { get; } = Guid.NewGuid();
    public string BrandTradeName => $"Zykomentin {Token} 1g";
    public string BrandIngredient => "amoxifixturil + clavulanic fixture";

    /// <summary>Carries NO source_row_id — a row from the superseded CSV load. The search must not offer it.</summary>
    public string LegacyDrugCode => $"TSTLEG-{Suffix}";
    public Guid LegacyDrugId { get; } = Guid.NewGuid();
    public string LegacyTradeName => $"Zykomentin {Token} legacy 1g";
    public Guid SensitiveExamId { get; } = Guid.NewGuid();
    public Guid StandardExamId { get; } = Guid.NewGuid();
    public Guid RetiredExamId { get; } = Guid.NewGuid();

    /// <summary>The retired type's own short code. Exposed so a test can ask the catalogue about it BY CODE —
    /// the price route is keyed on codes, not on ids, because an order line carries a code and only carries an
    /// examination_type_id if it was written after 14.6.</summary>
    public string RetiredExamCode => $"EXR-{Suffix}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MasterData"] = Db,
            ["Events:UseInMemoryOutbox"] = "true",
        }));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(s =>
        {
            s.AddAuthentication(MasterDataTestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, MasterDataTestAuth>(MasterDataTestAuth.SchemeName, _ => { });
            s.RemoveAll<IHostedService>();
        });
    }

    /// <summary>Any authenticated clinical caller holding <c>masterdata:read</c> — the scope every clinical
    /// role carries (26.1). MasterDataAuthzTests records why it is granted so broadly.</summary>
    public HttpClient ClinicalClient() => ClientWithScopes(MasterDataScopes.Read);

    /// <summary>An authenticated caller carrying exactly the given scopes — none, to test the gate.</summary>
    public HttpClient ClientWithScopes(params string[] scopes)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", "11111111-1111-1111-1111-111111111111");
        c.DefaultRequestHeaders.Add("X-Test-Role", "doctor");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "t-masterdata");
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        if (scopes.Length > 0) c.DefaultRequestHeaders.Add("X-Test-Scope", string.Join(' ', scopes));
        return c;
    }

    public async Task SeedAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        db.IcdCodes.Add(new IcdCode
        {
            Code = IcdCode, Title = "Reference fixture diagnosis", Chapter = "XXI",
            IsBillable = true, Icd11Map = "QA00", SourceRelease = Release,
        });
        db.IcdCodes.Add(new IcdCode
        {
            Code = IcdBlockCode, Title = "Reference fixture block heading", Chapter = "XXI",
            IsBillable = false, SourceRelease = Release,
        });
        db.CptCodes.Add(new CptCode
        {
            Code = CptCode, Description = "Reference fixture procedure", Category = "Fixture", SourceRelease = Release,
        });
        db.AtcClasses.Add(new AtcClass { AtcCode = AtcCode, Title = "Test substance", Level = 5, SourceRelease = Release });
        db.AtcClasses.Add(new AtcClass { AtcCode = OtherAtcCode, Title = "Other substance", Level = 5, SourceRelease = Release });
        db.AtcClasses.Add(new AtcClass { AtcCode = VaccineAtcCode, Title = "Test vaccine", Level = 5, SourceRelease = Release });
        // Saved in dependency order by hand. The model declares no navigation between these tables, so EF has
        // no graph to sort by and emits the inserts in the order they were tracked — which the real foreign
        // keys (drug → atc_class, drug_interaction → drug) then reject.
        await db.SaveChangesAsync();

        // SourceRowId is set on every fixture the typeahead is expected to find: /drugs/search serves the
        // CURRENT market list, and a fixture without one is invisible to it by design (see LegacyDrug below).
        db.Drugs.Add(new Drug
        {
            DrugId = DrugAId, DrugCode = DrugACode, Name = "Testolol 50mg", NameAr = "تستولول ٥٠",
            ScientificName = "testolol", Manufacturer = "Fixture Pharma", Form = "Tablet", Strength = "50mg",
            AtcCode = AtcCode, PriceEgp = 42.50m, SourceRelease = Release, SourceRowId = $"row-a-{Suffix}",
        });
        db.Drugs.Add(new Drug
        {
            DrugId = DrugBId, DrugCode = DrugBCode, Name = "Testolol generic 50mg", AtcCode = AtcCode,
            Form = "Tablet", Strength = "50mg", SourceRelease = Release, SourceRowId = $"row-b-{Suffix}",
        });
        db.Drugs.Add(new Drug
        {
            DrugId = DrugCId, DrugCode = DrugCCode, Name = "Unrelatide 10mg", AtcCode = OtherAtcCode,
            Form = "Capsule", Strength = "10mg", SourceRelease = Release, SourceRowId = $"row-c-{Suffix}",
        });
        db.Drugs.Add(new Drug
        {
            DrugId = VaccineDrugId, DrugCode = VaccineDrugCode, Name = VaccineDrugName,
            NameAr = "لقاح فيكستشرفاكس", AtcCode = VaccineAtcCode,
            Form = "Vial", Strength = "20mcg/ml", SourceRelease = Release, SourceRowId = $"row-v-{Suffix}",
        });
        db.Drugs.Add(new Drug
        {
            DrugId = BrandDrugId, DrugCode = BrandDrugCode, Name = BrandTradeName,
            NameAr = "زيكومنتين", ScientificName = BrandIngredient, AtcCode = AtcCode,
            Form = "Tablet", Strength = "1g", PriceEgp = 210.00m,
            SourceRelease = Release, SourceRowId = $"row-br-{Suffix}",
        });
        db.Drugs.Add(new Drug
        {
            // Deliberately NO SourceRowId. 8,998 real rows are in this state after the workbook load.
            DrugId = LegacyDrugId, DrugCode = LegacyDrugCode, Name = LegacyTradeName,
            ScientificName = BrandIngredient, AtcCode = AtcCode, Form = "Tablet", Strength = "1g",
            SourceRelease = Release,
        });
        await db.SaveChangesAsync();

        // The amoxicillin fixture, on the real J01CA04. The ATC class row may or may not already be in the
        // catalogue depending on whether the workbook has been loaded into this database, so it is inserted
        // only when absent — and it is NOT tagged with this run's source release, because a row the
        // catalogue owns must survive this test's cleanup.
        if (!await db.AtcClasses.AnyAsync(x => x.AtcCode == AmoxicillinAtcCode))
        {
            db.AtcClasses.Add(new AtcClass
            {
                AtcCode = AmoxicillinAtcCode, Title = "Amoxicillin", Level = 5, SourceRelease = Release,
            });
            await db.SaveChangesAsync();
        }

        db.Drugs.Add(new Drug
        {
            DrugId = AmoxicillinDrugId, DrugCode = AmoxicillinDrugCode, Name = $"Amoxifixture {Token} 500mg",
            ScientificName = "amoxicillin", AtcCode = AmoxicillinAtcCode, Form = "Capsule", Strength = "500mg",
            SourceRelease = Release, SourceRowId = $"row-amx-{Suffix}",
        });
        await db.SaveChangesAsync();

        // The decomposition row. 'amoxicillin' is seeded by migration 0009, so this exercises the real
        // ingredient key the curated allergen mapping points at rather than one the test invented.
        db.Set<DrugIngredient>().Add(new DrugIngredient
        {
            DrugId = AmoxicillinDrugId, IngredientKey = "amoxicillin", Ordinal = 0,
            Strength = "500mg", SourceRelease = Release,
        });
        await db.SaveChangesAsync();

        // Indications for the brand only, so hasIndicationData is asserted both ways. Category-level codes,
        // as the real source supplies them.
        db.DrugIndications.Add(new DrugIndication
        {
            IndicationId = Guid.NewGuid(), DrugId = BrandDrugId, IcdCode = IcdCode[..3],
            Source = "ATC + drug class", SourceRelease = Release,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // 28.3 — the interaction fixture is INGREDIENT-level, because the rule model is. Two fixture
        // molecules, one in each product, and one rule between them. The previous fixture wrote a
        // drug_interaction row keyed on the two PRODUCT ids, which is the model phase 28 retired: it needed
        // a row per pair of brands and so held zero rows in production for ever.
        db.Set<Ingredient>().Add(new Ingredient
        {
            IngredientId = Guid.NewGuid(), IngredientKey = FixtureIngredientA, NameEn = "Testolol",
            Source = "fixture", SourceRelease = Release, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Set<Ingredient>().Add(new Ingredient
        {
            IngredientId = Guid.NewGuid(), IngredientKey = FixtureIngredientB, NameEn = "Unrelatide",
            Source = "fixture", SourceRelease = Release, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        db.Set<DrugIngredient>().Add(new DrugIngredient
        { DrugId = DrugAId, IngredientKey = FixtureIngredientA, SourceRelease = Release });
        db.Set<DrugIngredient>().Add(new DrugIngredient
        { DrugId = DrugBId, IngredientKey = FixtureIngredientB, SourceRelease = Release });

        db.Set<InteractionRule>().Add(new InteractionRule
        {
            RuleId = Guid.NewGuid(),
            SubjectKind = RuleSubjectKind.Ingredient, SubjectValue = FixtureIngredientA,
            ObjectKind = RuleSubjectKind.Ingredient, ObjectValue = FixtureIngredientB,
            Severity = InteractionSeverity.Major,
            MechanismEn = "Fixture mechanism", MechanismAr = "آلية اختبارية",
            ClinicalEffectEn = "Fixture effect", ClinicalEffectAr = "أثر اختباري",
            ManagementEn = "Fixture management", ManagementAr = "تدبير اختباري",
            Onset = InteractionOnset.Unknown, EvidenceLevel = EvidenceLevel.Established,
            Citation = "Fixture citation", Source = "fixture", SourceRelease = Release,
            ReviewedBy = "fixture pharmacist", ReviewedAt = DateTimeOffset.UtcNow, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.ExaminationTypes.Add(new ExaminationType
        {
            ExaminationTypeId = SensitiveExamId, Code = $"EXS-{Suffix}", NameEn = "Mental health assessment",
            NameAr = "تقييم الصحة النفسية", Category = ExamCategory.Assessment, DefaultCodeSystem = "CPT",
            DefaultCode = CptCode, SensitivityLevel = SensitivityLevel.HighlySensitive,
            SensitiveCategory = SensitiveCategory.MentalHealth, Status = "Active",
        });
        db.ExaminationTypes.Add(new ExaminationType
        {
            ExaminationTypeId = StandardExamId, Code = $"EXO-{Suffix}", NameEn = "Ordinary panel",
            NameAr = "تحليل عادي", Category = ExamCategory.Lab, DefaultCodeSystem = "LOINC",
            SensitivityLevel = SensitivityLevel.Standard, Status = "Active",
        });
        db.ExaminationTypes.Add(new ExaminationType
        {
            ExaminationTypeId = RetiredExamId, Code = $"EXR-{Suffix}", NameEn = "Retired type",
            NameAr = "نوع متقاعد", Category = ExamCategory.Lab, SensitivityLevel = SensitivityLevel.Standard,
            Status = "Retired",
        });
        await db.SaveChangesAsync();
    }

    public async Task CleanupAsync()
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM masterdata.interaction_rule WHERE source_release = {0}; " +
            "DELETE FROM masterdata.drug_ingredient WHERE source_release = {0}; " +
            "DELETE FROM masterdata.drug_interaction WHERE source_release = {0}; " +
            "DELETE FROM masterdata.drug_indication WHERE source_release = {0}; " +
            "DELETE FROM masterdata.examination_type WHERE code LIKE {1}; " +
            "DELETE FROM masterdata.drug WHERE source_release = {0}; " +
            "DELETE FROM masterdata.atc_class WHERE source_release = {0}; " +
            "DELETE FROM masterdata.cpt_code WHERE source_release = {0}; " +
            "DELETE FROM masterdata.icd_code WHERE source_release = {0}; " +
            "DELETE FROM masterdata.ingredient WHERE source_release = {0};",
            Release, $"EX%-{Suffix}");
    }

    public static MasterDataDbContext Ctx() =>
        new(new DbContextOptionsBuilder<MasterDataDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);
}

/// <summary>Builds a principal from X-Test-* headers. The catalogue asks only for a token.</summary>
public sealed class MasterDataTestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("sub", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Role", out var role)) claims.Add(new Claim("role", role.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Tenant", out var tenant)) claims.Add(new Claim("tenant_id", tenant.ToString()));
        // 26.1 — the catalogue is scope-gated now, so a test caller has to be able to carry one (and to
        // deliberately omit it, which is how the gate itself is tested).
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        // Every service sets Auth:ProtectedScopeRequiresMfa=true, so a scope-gated endpoint also wants an
        // MFA-backed token. Same X-Test-Mfa convention the other services' handlers use.
        if (Request.Headers.ContainsKey("X-Test-Mfa")) claims.Add(new Claim("amr", "otp"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
