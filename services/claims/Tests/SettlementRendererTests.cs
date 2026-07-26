using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;

namespace Mersal.Claims.Tests;

/// <summary>Content-safety tests for the settlement-advice renderer (10b.8): parse the GENERATED CSV / XLSX / PDF bytes
/// (not just the DTO) and assert the absence of every clinical field name/value — codes + amounts only. Also proves the
/// content hash is stable for identical input and changes when the totals change.</summary>
public class SettlementRendererTests
{
    private static readonly string[] Clinical =
        ["diagnosis", "icd", "clinical", "emrnote", "emr_note", "symptom", "allergy", "soap", "vital", "resultvalue"];

    private static readonly Guid FixedPayee = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset FixedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static SettlementProjection Projection(decimal net = 160m) => new(
        "BAT-2026-000001", FixedPayee, null, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31),
        "officer", FixedAt, 1,
        [new SettlementLineRow("CLM-2026-000001", "CPT", "80053", 1, 200m, 180m, 160m, -20m, "Adjusted", [ReasonCodes.NoTariff]),
         new SettlementLineRow("CLM-2026-000002", "DRUG", "N02BE01", 2, 50m, 40m, 0m, 0m, "Denied", [ReasonCodes.LimitExceeded])],
        250m, 220m, 160m, -20m, 50m, net);

    [Theory]
    [InlineData("CSV")]
    [InlineData("XLSX")]
    [InlineData("PDF")]
    public void No_export_format_contains_any_clinical_field(string format)
    {
        var file = SettlementRenderer.Render(Projection(), format);
        var text = ExtractText(file).ToLowerInvariant();
        foreach (var token in Clinical)
            text.Should().NotContain(token, $"a {format} settlement export must carry no clinical field");
    }

    [Fact]
    public void The_csv_carries_the_expected_non_clinical_columns_and_totals()
    {
        var csv = Encoding.UTF8.GetString(SettlementRenderer.Render(Projection(), "CSV").Bytes);
        csv.Should().Contain("80053").And.Contain("NetPayable").And.Contain("160");
        csv.Should().Contain("NO_TARIFF"); // coded reasons are financial, not clinical
    }

    [Fact]
    public void The_content_hash_is_stable_for_identical_input_and_changes_with_the_totals()
    {
        SettlementRenderer.ContentHash(Projection(160m)).Should().Be(SettlementRenderer.ContentHash(Projection(160m)));
        SettlementRenderer.ContentHash(Projection(160m)).Should().NotBe(SettlementRenderer.ContentHash(Projection(140m)));
    }

    private static string ExtractText(RenderedFile file) => file.Format switch
    {
        "XLSX" => UnzipSheet(file.Bytes),
        _ => Encoding.UTF8.GetString(file.Bytes), // CSV + PDF are UTF-8 text/streams — searchable directly
    };

    private static string UnzipSheet(byte[] xlsx)
    {
        using var ms = new MemoryStream(xlsx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml")))
        {
            using var r = new StreamReader(entry.Open());
            sb.Append(r.ReadToEnd());
        }
        return sb.ToString();
    }
}
