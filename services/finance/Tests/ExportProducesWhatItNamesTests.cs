using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Finance.Domain;
using Mersal.Finance.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Finance.Tests;

/// <summary>
/// The export produces the report it names, in the format it names, or it refuses. Design 49 §2.
/// </summary>
/// <remarks>
/// <para>Three controls on the Exports screen used to do nothing, and two of them corrupted the audit
/// trail rather than merely disappointing the operator.</para>
/// <para><b>The report was ignored.</b> The handler ran <c>UtilizationAsync</c> whatever was asked for;
/// <c>Report</c> named the file and the high-severity <c>data.export</c> audit event and was otherwise
/// unread. Asking for settlements produced utilization rows in a file called <c>settlement-….csv</c> and
/// wrote an audit record asserting a settlement export that did not happen. A record naming an action nobody
/// performed is worse than no record, because the record is what an auditor trusts.</para>
/// <para><b>The format was ignored.</b> Always <c>text/csv</c> — while <c>ExportRecord.Format</c> stored the
/// CLAIMED format, so the export ledger asserted spreadsheets that were never generated.</para>
/// </remarks>
[Collection("finance-db")]
public class ExportProducesWhatItNamesTests
{
    private static async Task<HttpClient> ExporterAsync(FinanceApiFactory app)
    {
        var c = app.As(FinanceApiFactory.OfficerSub, "finance", "finance:read finance:write finance:export");
        await Task.CompletedTask;
        return c;
    }

    private static object Request(string report, string format = "csv") => new
    {
        report,
        format,
        from = "2026-07-01",
        to = "2026-07-31",
    };

    /// <summary>
    /// The three reports produce three DIFFERENT files. Asserting only that each returns 200 would pass on
    /// the defective service, which returned 200 for all three — with identical bodies.
    /// </summary>
    [SkippableFact]
    public async Task Each_report_produces_its_own_columns_rather_than_utilization_under_three_names()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            var client = await ExporterAsync(app);

            var utilization = await client.PostAsJsonAsync("/api/v1/finance/exports", Request("utilization"));
            var settlement = await client.PostAsJsonAsync("/api/v1/finance/exports", Request("settlement"));
            var summary = await client.PostAsJsonAsync("/api/v1/finance/exports", Request("summary"));

            foreach (var r in new[] { utilization, settlement, summary })
                r.StatusCode.Should().Be(HttpStatusCode.OK);

            var utilHeader = (await utilization.Content.ReadAsStringAsync()).Split('\n')[0].Trim();
            var setHeader = (await settlement.Content.ReadAsStringAsync()).Split('\n')[0].Trim();
            var sumHeader = (await summary.Content.ReadAsStringAsync()).Split('\n')[0].Trim();

            utilHeader.Should().StartWith("service_code,service_line,coverage_category");
            // The settlement file is line-grained and carries the price source — the fact a reviewer needs
            // and the one a utilization file cannot contain.
            setHeader.Should().StartWith("settlement_no,provider_ref");
            setHeader.Should().Contain("price_source");
            sumHeader.Should().Be("dimension,key,delivered_qty,spend");

            // The precise regression: three reports, three shapes. They were one.
            new[] { utilHeader, setHeader, sumHeader }.Distinct().Should().HaveCount(3);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>An unknown report is refused, not quietly served as utilization — a fallback would read
    /// exactly like the original defect from the outside.</summary>
    [SkippableFact]
    public async Task An_unknown_report_is_refused_rather_than_falling_back()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            var client = await ExporterAsync(app);
            var res = await client.PostAsJsonAsync("/api/v1/finance/exports", Request("payroll"));
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await res.Content.ReadAsStringAsync()).Should().Contain("unknown-report");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// XLSX is refused rather than silently answered with a CSV.
    /// </summary>
    /// <remarks>
    /// The portal no longer offers it, but the portal is not the only caller — and a silent substitution is
    /// how the export ledger came to record spreadsheets that never existed.
    /// </remarks>
    [SkippableFact]
    public async Task A_format_this_endpoint_does_not_produce_is_refused()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            var client = await ExporterAsync(app);
            var res = await client.PostAsJsonAsync("/api/v1/finance/exports", Request("utilization", "xlsx"));
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await res.Content.ReadAsStringAsync()).Should().Contain("unsupported-format");

            // And nothing was written to the ledger for a file that was not produced.
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var recorded = await db.Exports.AsNoTracking().Where(e => e.TenantId == app.Tenant).ToListAsync();
            recorded.Should().NotContain(e => e.Format == "xlsx");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The audited row count reaches the caller on <c>X-Row-Count</c>, and it counts the rows in the file.
    /// </summary>
    /// <remarks>
    /// The body is a file, so there is nowhere in it for the figure. The old client reported a count while
    /// downloading nothing; this is how the repaired one still gets the number it shows the operator — and
    /// it must be the same number the <c>data.export</c> audit event recorded, or the screen and the trail
    /// disagree about the same act.
    /// </remarks>
    [SkippableFact]
    public async Task The_row_count_is_on_a_header_and_matches_the_file_and_the_ledger()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            var client = await ExporterAsync(app);
            var res = await client.PostAsJsonAsync("/api/v1/finance/exports", Request("utilization"));
            res.StatusCode.Should().Be(HttpStatusCode.OK);

            res.Headers.TryGetValues("X-Row-Count", out var values).Should().BeTrue(
                "the row count has nowhere to live in a file body");
            var reported = int.Parse(values!.Single());

            var body = await res.Content.ReadAsStringAsync();
            var dataRows = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;   // minus the header
            reported.Should().Be(dataRows);

            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var record = await db.Exports.AsNoTracking()
                .Where(e => e.TenantId == app.Tenant && e.Report == "utilization")
                .OrderByDescending(e => e.CreatedAt).FirstAsync();
            record.RowCount.Should().Be(reported);
            record.Format.Should().Be("csv", "the ledger records what was PRODUCED, not what was asked for");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The settlement list reports how many there really are, so a capped page is not read as the
    /// whole answer (invariant 31).</summary>
    [SkippableFact]
    public async Task The_settlement_list_reports_the_true_count_on_a_header()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            var client = app.OfficerClient();
            var res = await client.GetAsync("/api/v1/finance/settlements");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Headers.TryGetValues("X-Total-Count", out var values).Should().BeTrue();
            int.Parse(values!.Single()).Should().BeGreaterThanOrEqualTo(0);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The settlement view carries who submitted it — the field that lets a screen honour segregation of
    /// duties BEFORE offering the button, rather than only in the 409 that follows the click.
    /// </summary>
    [SkippableFact]
    public async Task A_submitted_settlement_names_its_submitter()
    {
        Skip.If(FinanceApiFactory.Db is null, "FINANCE_TEST_DB not set — DB integration test skipped.");
        await using var app = new FinanceApiFactory();
        try
        {
            var officer = app.OfficerClient();
            var created = await officer.PostAsJsonAsync("/api/v1/finance/settlements", new
            {
                providerId = Guid.NewGuid(),
                periodStart = "2026-07-01",
                periodEnd = "2026-07-31",
            });
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var draft = await created.Content.ReadFromJsonAsync<SettlementView>();
            draft!.SubmittedBy.Should().BeNull("a draft has not been submitted by anyone");

            var submitted = await officer.PostAsJsonAsync(
                $"/api/v1/finance/settlements/{draft.SettlementId}/submit", new { });
            submitted.StatusCode.Should().Be(HttpStatusCode.OK);
            var view = await submitted.Content.ReadFromJsonAsync<SettlementView>();
            view!.SubmittedBy.Should().Be(FinanceApiFactory.OfficerSub);
            view.ApprovedBy.Should().BeNull();
        }
        finally { await app.CleanupAsync(); }
    }
}
