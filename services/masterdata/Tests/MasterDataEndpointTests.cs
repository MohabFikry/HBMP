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
    public Guid DrugAId { get; } = Guid.NewGuid();
    public Guid DrugBId { get; } = Guid.NewGuid();
    public Guid DrugCId { get; } = Guid.NewGuid();
    public Guid VaccineDrugId { get; } = Guid.NewGuid();
    public Guid SensitiveExamId { get; } = Guid.NewGuid();
    public Guid StandardExamId { get; } = Guid.NewGuid();
    public Guid RetiredExamId { get; } = Guid.NewGuid();

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

    /// <summary>Any authenticated clinical caller. The catalogue requires a token and no scope beyond it —
    /// deliberately, and MasterDataAuthzTests records why.</summary>
    public HttpClient ClinicalClient()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", "11111111-1111-1111-1111-111111111111");
        c.DefaultRequestHeaders.Add("X-Test-Role", "doctor");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "t-masterdata");
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

        db.Drugs.Add(new Drug
        {
            DrugId = DrugAId, DrugCode = DrugACode, Name = "Testolol 50mg", NameAr = "تستولول ٥٠",
            ScientificName = "testolol", Manufacturer = "Fixture Pharma", Form = "Tablet", Strength = "50mg",
            AtcCode = AtcCode, PriceEgp = 42.50m, SourceRelease = Release,
        });
        db.Drugs.Add(new Drug
        {
            DrugId = DrugBId, DrugCode = DrugBCode, Name = "Testolol generic 50mg", AtcCode = AtcCode,
            Form = "Tablet", Strength = "50mg", SourceRelease = Release,
        });
        db.Drugs.Add(new Drug
        {
            DrugId = DrugCId, DrugCode = DrugCCode, Name = "Unrelatide 10mg", AtcCode = OtherAtcCode,
            Form = "Capsule", Strength = "10mg", SourceRelease = Release,
        });
        db.Drugs.Add(new Drug
        {
            DrugId = VaccineDrugId, DrugCode = VaccineDrugCode, Name = VaccineDrugName,
            NameAr = "لقاح فيكستشرفاكس", AtcCode = VaccineAtcCode,
            Form = "Vial", Strength = "20mcg/ml", SourceRelease = Release,
        });
        await db.SaveChangesAsync();

        db.DrugInteractions.Add(new DrugInteraction
        {
            InteractionId = Guid.NewGuid(), DrugAId = DrugAId, DrugBId = DrugBId,
            Severity = InteractionSeverity.Major, Description = "Fixture interaction", SourceRelease = Release,
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
            "DELETE FROM masterdata.drug_interaction WHERE source_release = {0}; " +
            "DELETE FROM masterdata.examination_type WHERE code LIKE {1}; " +
            "DELETE FROM masterdata.drug WHERE source_release = {0}; " +
            "DELETE FROM masterdata.atc_class WHERE source_release = {0}; " +
            "DELETE FROM masterdata.cpt_code WHERE source_release = {0}; " +
            "DELETE FROM masterdata.icd_code WHERE source_release = {0};",
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

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
