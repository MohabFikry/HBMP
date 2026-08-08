using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using Mersal.MasterData.Domain;
using Mersal.MasterData.Loader;

namespace Mersal.MasterData.Tests;

/// <summary>
/// Ingestion tests for "Master Lists/egyptian-drug-list_5.xlsx" (phase 26.1).
/// </summary>
/// <remarks>
/// The real workbook resolves every one of its 874 ICD categories, so the unmatched-code path never fires
/// against production data. That is exactly why it is tested here: the failure it guards against — a drug
/// silently losing its indications and reporting "not checked" forever — is invisible when it happens.
/// </remarks>
public class DrugListLoaderTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    // The categories the fixtures use, standing in for a loaded masterdata.icd_code.
    private static readonly string[] KnownIcds = ["E11", "E11.9", "J01", "J01.0", "K21", "M54"];

    [Fact]
    public void Drug_id_is_derived_from_the_source_row_id_so_reloads_are_stable()
    {
        var a = MasterDataNormalize.DrugId("13245");
        var b = MasterDataNormalize.DrugId(" 13245 ");

        a.Should().Be(b, "whitespace in the source must not mint a second identity");
        a.Should().NotBe(Guid.Empty);
        a.Should().NotBe(MasterDataNormalize.DrugId("13246"));
    }

    [Theory]
    [InlineData("E11.9", "E11")]
    [InlineData("E119", "E11")]
    [InlineData("E11", "E11")]
    [InlineData(" j01.0 ", "J01")]
    public void Icd_category_is_the_first_three_characters(string input, string expected)
        => MasterDataNormalize.IcdCategory(input).Should().Be(expected);

    [Fact]
    public void A_specific_diagnosis_matches_a_category_level_indication()
    {
        // The whole reason IcdCategory exists: the drug list records "E11", the encounter records "E11.9".
        MasterDataNormalize.IcdCategory("E11.9").Should().Be(MasterDataNormalize.IcdCategory("E11"));
    }

    [Fact]
    public void Placeholder_only_indications_produce_no_rows()
    {
        // Z76 is the source's filler for "nothing is recorded here". Storing it would let a product with no
        // clinical data render as checked.
        var row = new DrugListXlsxRow { RelatedIcds = "Z76", IcdBasis = "placeholder" };

        Mappers.ToDrugIndications(row, Guid.NewGuid(), "R1").Should().BeEmpty();
    }

    [Fact]
    public void Placeholder_alongside_real_codes_is_dropped_but_the_rest_are_kept()
    {
        var row = new DrugListXlsxRow { RelatedIcds = "E11, Z76, J01", IcdBasis = "ATC + drug class" };

        Mappers.ToDrugIndications(row, Guid.NewGuid(), "R1")
            .Select(i => i.IcdCode).Should().Equal("E11", "J01");
    }

    [Fact]
    public void Indications_are_normalised_to_categories_and_deduped()
    {
        var row = new DrugListXlsxRow { RelatedIcds = "e11.9, E11, J01 , J01.0", IcdBasis = "ATC" };

        var result = Mappers.ToDrugIndications(row, Guid.NewGuid(), "R1").ToList();

        result.Select(i => i.IcdCode).Should().Equal("E11", "J01");
        result.Should().OnlyContain(i => i.Source == "ATC", "per-row provenance is carried, not invented");
        result.Should().OnlyContain(i => !i.IsPrimary, "the source expresses no ranking over indications");
    }

    [Fact]
    public void Unmatched_icd_codes_are_reported_rather_than_silently_dropped()
    {
        var path = WriteWorkbook([
            Row("1", "Glucophage 500", atc: "A10BA02", icds: "E11, ZZ9", basis: "ATC + drug class"),
        ]);

        var load = Loaders.LoadDrugList(path, "R1", KnownIcds);

        load.Indications.Should().ContainSingle().Which.IcdCode.Should().Be("E11");
        load.IndicationReport.SkipReasons.Should().ContainKey("icd-unmatched");
        load.IndicationReport.Notes.Should().Contain(n => n.Contains("ZZ9"),
            "an unmatched code must be named in the report, not just counted");
    }

    [Fact]
    public void A_drug_that_loses_every_indication_is_named_in_the_report()
    {
        var path = WriteWorkbook([
            Row("1", "Mystery Tonic", atc: "", icds: "ZZ9, YY8", basis: "drug class"),
        ]);

        var load = Loaders.LoadDrugList(path, "R1", KnownIcds);

        load.Indications.Should().BeEmpty();
        load.IndicationReport.Notes.Should().Contain(n => n.Contains("NONE of it resolved"),
            "this drug will report \"not checked\" indefinitely and that must be visible at load time");
        load.IndicationReport.Notes.Should().Contain(n => n.Contains("MYSTERY-TONIC"));
    }

    [Fact]
    public void Drugs_with_no_indication_data_are_counted_separately_from_unmatched_ones()
    {
        var path = WriteWorkbook([
            Row("1", "Filler Syrup", atc: "", icds: "Z76", basis: "placeholder"),
        ]);

        var load = Loaders.LoadDrugList(path, "R1", KnownIcds);

        load.Indications.Should().BeEmpty();
        load.IndicationReport.Notes.Should().Contain(n => n.Contains("carry no indication data"));
        load.IndicationReport.SkipReasons.Should().NotContainKey("icd-unmatched",
            "no data is a different finding from data that failed to resolve");
    }

    [Fact]
    public void Loading_twice_yields_identical_ids_and_counts()
    {
        var path = WriteWorkbook([
            Row("1", "Glucophage 500", atc: "A10BA02", icds: "E11", basis: "ATC"),
            Row("2", "Augmentin 1g", atc: "J01CR02", icds: "J01, K21", basis: "ATC + drug class"),
        ]);

        var first = Loaders.LoadDrugList(path, "R1", KnownIcds);
        var second = Loaders.LoadDrugList(path, "R1", KnownIcds);

        second.Drugs.Select(d => d.DrugId).Should().Equal(first.Drugs.Select(d => d.DrugId));
        second.Indications.Select(i => i.IndicationId).Should().Equal(first.Indications.Select(i => i.IndicationId));
        first.Indications.Should().HaveCount(3);
    }

    [Fact]
    public void Strength_falls_back_to_volume_or_weight()
    {
        var path = WriteWorkbook([
            Row("1", "Tabs", atc: "", icds: "E11", basis: "ATC", strength: "500 mg"),
            Row("2", "Syrup", atc: "", icds: "E11", basis: "ATC", strength: "", volume: "120 ml"),
            Row("3", "Neither", atc: "", icds: "E11", basis: "ATC", strength: "", volume: ""),
        ]);

        var load = Loaders.LoadDrugList(path, "R1", KnownIcds);

        load.Drugs.Single(d => d.Name == "Tabs").Strength.Should().Be("500 mg");
        load.Drugs.Single(d => d.Name == "Syrup").Strength.Should().Be("120 ml");
        load.Drugs.Single(d => d.Name == "Neither").Strength.Should().BeNull();
    }

    [Fact]
    public void The_workbook_carries_no_arabic_name_and_the_report_says_so()
    {
        var path = WriteWorkbook([Row("1", "Tabs", atc: "", icds: "E11", basis: "ATC")]);

        var load = Loaders.LoadDrugList(path, "R1", KnownIcds);

        load.Drugs.Should().OnlyContain(d => d.NameAr == null);
        load.DrugReport.Notes.Should().Contain(n => n.Contains("name_ar: 0/"));
    }

    [Fact]
    public void A_missing_required_column_fails_loudly_rather_than_loading_nulls()
    {
        var headers = DrugListColumns.Required.Where(c => c != DrugListColumns.RelatedIcds).ToArray();
        var path = WriteWorkbook([], headers);

        var act = () => Loaders.LoadDrugList(path, "R1", KnownIcds).Drugs;

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Related ICDs*", "a renamed column must stop the load, not empty the indications");
    }

    [Fact]
    public void A_missing_sheet_names_the_sheets_that_are_present()
    {
        var path = WriteWorkbook([], sheetName: "Something Else");

        var act = () => Loaders.LoadDrugList(path, "R1", KnownIcds).Drugs;

        act.Should().Throw<InvalidDataException>().WithMessage("*Something Else*");
    }

    // ------------------------------------------------------------------ fixtures

    private static Dictionary<string, string> Row(
        string id, string tradeName, string atc, string icds, string basis,
        string strength = "", string volume = "", string ingredient = "test ingredient") =>
        new()
        {
            [DrugListColumns.SourceRowId] = id,
            [DrugListColumns.TradeNameEn] = tradeName,
            [DrugListColumns.PriceEgp] = "40",
            [DrugListColumns.ActiveIngredient] = ingredient,
            [DrugListColumns.Manufacturer] = "test manufacturer",
            [DrugListColumns.AtcCode] = atc,
            [DrugListColumns.AtcL1] = "L1", [DrugListColumns.AtcL2] = "L2", [DrugListColumns.AtcL3] = "L3",
            [DrugListColumns.AtcL4] = "L4", [DrugListColumns.AtcL5] = "L5",
            [DrugListColumns.RelatedIcds] = icds,
            [DrugListColumns.IcdCount] = icds.Split(',', StringSplitOptions.RemoveEmptyEntries).Length.ToString(),
            [DrugListColumns.IcdBasis] = basis,
            [DrugListColumns.VolumeWeight] = volume,
            [DrugListColumns.Strength] = strength,
            [DrugListColumns.DosageForm] = "tablet",
        };

    /// <summary>
    /// Writes a real xlsx (shared strings and all) so the streaming reader is exercised the way the
    /// production workbook exercises it, rather than against a hand-rolled stand-in.
    /// </summary>
    private string WriteWorkbook(
        IReadOnlyList<Dictionary<string, string>> rows,
        string[]? headers = null,
        string sheetName = "Drug List")
    {
        headers ??= DrugListColumns.Required;
        var path = Path.Combine(Path.GetTempPath(), $"drug-list-{Guid.NewGuid():N}.xlsx");
        _tempFiles.Add(path);

        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = doc.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var stringTable = workbookPart.AddNewPart<SharedStringTablePart>();
        stringTable.SharedStringTable = new SharedStringTable();
        var stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        int Intern(string text)
        {
            if (stringIndex.TryGetValue(text, out var i)) return i;
            stringTable.SharedStringTable.Append(new SharedStringItem(new Text(text)));
            return stringIndex[text] = stringIndex.Count;
        }

        var sheetData = new SheetData();
        sheetData.Append(BuildRow(1, headers.Select(Intern)));
        var rowNumber = 2;
        foreach (var row in rows)
        {
            sheetData.Append(BuildRow(rowNumber++, headers.Select(h => Intern(row.GetValueOrDefault(h, "")))));
        }

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(sheetData);
        workbookPart.Workbook.AppendChild(new Sheets()).AppendChild(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = sheetName,
        });
        workbookPart.Workbook.Save();

        return path;
    }

    /// <summary>Emits cells with real A1-style references, the way Excel writes them.</summary>
    private static Row BuildRow(int rowNumber, IEnumerable<int> sharedStringIndexes)
    {
        var row = new Row { RowIndex = (uint)rowNumber };
        var column = 0;
        foreach (var index in sharedStringIndexes)
        {
            row.Append(new Cell
            {
                CellReference = $"{ColumnName(column++)}{rowNumber}",
                DataType = CellValues.SharedString,
                CellValue = new CellValue(index.ToString()),
            });
        }
        return row;
    }

    /// <summary>0 → "A", 25 → "Z", 26 → "AA".</summary>
    private static string ColumnName(int index)
    {
        var name = "";
        for (var n = index; n >= 0; n = n / 26 - 1) name = (char)('A' + n % 26) + name;
        return name;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles.Where(File.Exists)) File.Delete(f);
        GC.SuppressFinalize(this);
    }
}
