using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Mersal.MasterData.Tests;

/// <summary>
/// The catalogue routes nothing had ever called.
///
/// <para><b>Why this file exists.</b> `services/masterdata:Api` measured 65.9% against a 77% floor — a
/// fourteen-point regression since the floor was set on 2026-07-31, invisible because CI has been
/// billing-blocked since 2026-08-11 and a local `./coverage` directory that accumulates across runs only ever
/// reports coverage going up. Nineteen of the service's thirty-seven endpoints had no test.</para>
///
/// <para>These are the ones worth reaching first, and they share a shape: <b>every one of them is a
/// fail-closed contract another service leans on, and every one of them fails by answering confidently.</b>
/// A price route that returned 0 for an unpriced medicine reads at a counter as "this is free". A
/// `/procedure-types/{code}/validate` that said Ok to everything disables a check in orders-service and in
/// the composer. An `/exists` route that answered true for everything would break no test here and quietly
/// stop three other services validating anything. None of those failures raise an error anywhere.</para>
/// </summary>
[Collection("masterdata-db")]
public class CatalogueContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- money: null is not zero

    [SkippableFact]
    public async Task An_unpriced_medicine_comes_back_NULL_and_never_zero()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // An id that matches nothing at all: the caller still gets a row for it, priced null.
            var unknown = Guid.NewGuid();
            var r = await client.PostAsJsonAsync("/api/v1/drugs/prices/by-ids",
                new { drugIds = new[] { app.DrugAId, unknown } }, Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

            var items = (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("items").EnumerateArray().ToList();

            // EVERY id is answered for. A route that simply omitted what it could not find would leave the
            // caller to decide what an absent row means, and the two readings — "free" and "unknown" — are
            // opposite instructions to a pharmacist with a member in front of them.
            items.Should().HaveCount(2);

            var missing = items.Single(x => x.GetProperty("drugId").GetGuid() == unknown);
            missing.GetProperty("priceEgp").ValueKind.Should().Be(JsonValueKind.Null,
                "0 is 'this medicine is free' and null is 'we do not know what it costs'");

            var known = items.Single(x => x.GetProperty("drugId").GetGuid() == app.DrugAId);
            known.GetProperty("priceEgp").GetDecimal().Should().Be(42.50m);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_examination_price_is_found_by_the_billing_code_an_order_line_actually_carries()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // The catalogue is browsed by its own short code; an order line records the DEFAULT BILLING code,
            // and lines written before 14.6 carry no examination_type_id at all. A lookup that matched only
            // the short code would return nothing for every one of those — which reads as "free".
            var r = await client.PostAsJsonAsync("/api/v1/examination-types/prices/by-codes",
                new { codes = new[] { app.CptCode } }, Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

            var body = await r.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("currency").GetString().Should().Be("EGP");
            body.GetProperty("items").EnumerateArray().Should().NotBeEmpty(
                "the sensitive fixture's default code IS this CPT code");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_retired_examination_comes_back_UNPRICED_rather_than_missing()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // Retired means "no longer offered", so the price query excludes it — but the code asked about
            // still gets a row, with a null price and a null name. That is the same rule as the drug route:
            // every code asked about is answered for, and null means "we cannot quote this", never "free".
            //
            // This test first asserted an EMPTY list, which would have been the wrong contract: a caller
            // handed nothing has to decide for itself what the silence meant, and the two readings — "no
            // charge" and "we do not know" — are opposite things to say to a member.
            var r = await client.PostAsJsonAsync("/api/v1/examination-types/prices/by-codes",
                new { codes = new[] { app.RetiredExamCode } }, Web);

            var item = (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("items").EnumerateArray().Single();

            item.GetProperty("code").GetString().Should().Be(app.RetiredExamCode);
            item.GetProperty("priceEgp").ValueKind.Should().Be(JsonValueKind.Null,
                "a retired type cannot be quoted, and 0.00 at a counter reads as free");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- the codes other services validate on

    [SkippableFact]
    public async Task A_cpt_code_reports_its_section_and_an_unknown_one_is_a_404()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var ok = await client.GetAsync(new Uri($"/api/v1/cpt-codes/{app.CptCode}/section", UriKind.Relative));
            ok.StatusCode.Should().Be(HttpStatusCode.OK);

            // 404, not a default section. The section decides the VEHICLE — a procedure order or a referral —
            // so guessing one routes a beneficiary's care to the wrong queue.
            var missing = await client.GetAsync(new Uri("/api/v1/cpt-codes/99999X/section", UriKind.Relative));
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_exists_check_says_no_for_a_code_that_is_not_there()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // THE negative case. orders-service refuses a line this does not confirm, so an endpoint that
            // answered true for everything would break no test in orders — its guard would simply stop
            // guarding, silently, for every code on the platform.
            var yes = await client.GetAsync(new Uri($"/api/v1/cpt-codes/{app.CptCode}/exists", UriKind.Relative));
            (await yes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("exists").GetBoolean().Should().BeTrue();

            var no = await client.GetAsync(new Uri("/api/v1/cpt-codes/00000X/exists", UriKind.Relative));
            (await no.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("exists").GetBoolean().Should().BeFalse();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Icd_ancestors_answer_for_a_code_that_has_none_rather_than_failing()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // The ancestor table is populated by a curated load the fixture release does not carry, so this
            // asserts the SHAPE and the empty answer: a benefit rule written against a parent block asks this
            // route, and a 500 for an unmapped code would fail the rule closed in a way nobody could read.
            var r = await client.PostAsJsonAsync("/api/v1/icd-codes/ancestors",
                new { codes = new[] { app.IcdCode } }, Web);

            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());
            (await r.Content.ReadFromJsonAsync<JsonElement>()).ValueKind.Should().Be(JsonValueKind.Object);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- the browse routes

    [SkippableFact]
    public async Task The_atc_catalogue_filters_by_level_and_by_query()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var byCode = await client.GetAsync(new Uri($"/api/v1/atc-classes?q={app.AtcCode}", UriKind.Relative));
            byCode.StatusCode.Should().Be(HttpStatusCode.OK);
            (await byCode.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
                .Select(x => x.GetProperty("atcCode").GetString()).Should().Contain(app.AtcCode);

            // The level filter is the one that narrows a prescribing picker to substances. A filter that was
            // ignored would return the whole tree and look like a slow catalogue rather than a broken one.
            var wrongLevel = await client.GetAsync(new Uri($"/api/v1/atc-classes?level=1&q={app.AtcCode}", UriKind.Relative));
            (await wrongLevel.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
                .Should().BeEmpty("the fixture classes are level 5");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Procedure_types_are_listed_active_only_unless_asked_otherwise()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var r = await client.GetAsync(new Uri("/api/v1/procedure-types", UriKind.Relative));
            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

            var rows = (await r.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

            // The seeded catalogue is what the composer offers. Every row must be active, because an inactive
            // type on a picker is a procedure a centre can be asked for and cannot deliver.
            rows.Should().NotBeEmpty("the seeded OP-Procedure kinds are curated data, not a fixture");
            rows.Should().OnlyContain(x => x.GetProperty("isActive").GetBoolean());

            // Session-based kinds carry a default and a maximum; the flag and the numbers must agree, or the
            // composer offers a session count for a procedure that has no sessions.
            foreach (var row in rows.Where(x => x.GetProperty("isSessionBased").GetBoolean()))
                row.GetProperty("defaultSessions").ValueKind.Should().NotBe(JsonValueKind.Null);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_procedure_type_validation_refuses_a_code_outside_its_scope()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // Asked by the composer as the doctor picks, and RE-ASKED by orders-service on the write path,
            // because the composer's verdict is display state. Both callers refuse on a rejection — so a
            // route that waved everything through disables the check in two places at once and nothing
            // reports it.
            var r = await client.GetAsync(new Uri(
                $"/api/v1/procedure-types/Physiotherapy/validate?cptCode={app.CptCode}", UriKind.Relative));

            // A TYPED 422, not a 200 carrying ok:false — which is what this test first expected, and the
            // weaker of the two contracts. A refusal that arrives as a success status is one a caller can
            // ignore by reading only the status line, and the two callers here are in different services.
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

            var body = await r.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("reason").GetString().Should().Be("SectionNotAllowed");
            // The refusal names the type, the code and what the type DOES accept — a doctor mid-composition
            // can act on that; "invalid" would send them to a phone.
            body.GetProperty("detail").GetString().Should().Contain("Medicine");
            // And it is bilingual, because the composer shows it to an Arabic-reading prescriber (ADR-0042).
            body.GetProperty("detailAr").GetString().Should().NotBeNullOrWhiteSpace();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Orderable_services_report_the_vehicle_a_code_will_actually_create()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // Design 45 §2, invariant 3: the doctor picks a service and the SYSTEM decides the vehicle. This
            // is the route that publishes that verdict so the composer can say what will happen before the
            // doctor commits — an E/M code becomes a referral, a surgical one a procedure order.
            var r = await client.GetAsync(new Uri($"/api/v1/orderable-services?q={app.CptCode}", UriKind.Relative));
            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

            var items = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")
                .EnumerateArray().ToList();
            items.Should().NotBeEmpty();
            foreach (var i in items)
                i.GetProperty("vehicle").GetString().Should().NotBeNullOrWhiteSpace(
                    "a row with no vehicle leaves the composer guessing what the code creates");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- the prescribing look-ups

    [SkippableFact]
    public async Task Pack_facts_OMIT_an_id_the_catalogue_does_not_describe_and_that_is_deliberate()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            var unknown = Guid.NewGuid();
            var r = await client.PostAsJsonAsync("/api/v1/drugs/pack-facts/by-ids",
                new { drugIds = new[] { app.DrugAId, unknown } }, Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());

            var items = (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("items").EnumerateArray().ToList();

            // THE OPPOSITE CONVENTION FROM /drugs/prices/by-ids, ON PURPOSE, and this test exists to stop a
            // tidy-minded refactor making the two agree.
            //
            // The price route answers for every id because a missing price and a zero price are opposite
            // things to say at a counter. This one OMITS what the catalogue does not describe, because the
            // quantity check distinguishes three states and needs the absence to carry one of them: a missing
            // id is NotChecked naming the field, the whole call failing is Unavailable. Padding with rows of
            // nulls collapses the first into the second — and padding with defaults would be the guessed
            // quantity invariant 8 exists to forbid.
            items.Should().ContainSingle("the unknown id is absent, which is how 'not recorded' is said here");
            items[0].GetProperty("drugId").GetGuid().Should().Be(app.DrugAId);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Dosing_rules_and_indications_answer_with_a_shape_rather_than_a_failure()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // Neither fixture carries curated rules, and that is the case worth pinning: the prescribing
            // validator treats "no rule" and "could not ask" differently, and it can only do that if the
            // catalogue answers the first one cleanly instead of erroring.
            var dosing = await client.PostAsJsonAsync("/api/v1/dosing-rules/by-ids", new
            {
                drugIds = new[] { app.DrugAId },
                diagnosisIcdCodes = new[] { app.IcdCode },
                population = "Adult",
                route = "Oral",
            }, Web);
            dosing.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await dosing.Content.ReadAsStringAsync());

            var indications = await client.PostAsJsonAsync("/api/v1/drug-indications/by-ids",
                new { drugIds = new[] { app.DrugAId } }, Web);
            indications.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await indications.Content.ReadAsStringAsync());
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_contraindication_check_treats_unknown_pregnancy_as_unknown()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // The contract's own words: "Unknown must not count as pregnant — a rule firing on every patient
            // nobody has asked about would be dismissed within a day, and would stop meaning anything for the
            // patients it was written for."
            var r = await client.PostAsJsonAsync("/api/v1/contraindications/check-by-ids",
                new { drugIds = new[] { app.DrugAId }, isPregnant = false }, Web);

            r.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await r.Content.ReadAsStringAsync());
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_allergy_check_on_an_unknown_drug_code_is_a_404_not_a_clean_result()
    {
        Skip.If(MasterDataApiFactory.Db is null, "MASTERDATA_TEST_DB not set — DB integration test skipped.");
        await using var app = new MasterDataApiFactory();
        try
        {
            await app.SeedAsync();
            using var client = app.ClinicalClient();

            // THE fail-closed property. A drug the catalogue does not know cannot be checked against
            // anything, and answering "no allergy found" would be a clean bill of health for a comparison
            // that never happened — the same defect pass 5 found in the interaction checker.
            var r = await client.PostAsJsonAsync("/api/v1/allergies/check",
                new { drugCode = "NO-SUCH-DRUG", patientAllergenCodes = new[] { "ALG-PENICILLIN" } }, Web);

            r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await app.CleanupAsync(); }
    }
}
