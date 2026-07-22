using FluentAssertions;
using Mersal.MasterData.Domain;
using Mersal.MasterData.Loader;

namespace Mersal.MasterData.Tests;

public class MapperTests
{
    [Theory]
    [InlineData("chapter", false)]
    [InlineData("block", false)]
    [InlineData("category", true)]
    [InlineData("subcategory", true)]
    public void Icd_billable_only_for_leaf_types(string type, bool billable)
    {
        var e = Mappers.ToIcd(new IcdCsvRow { Code = "E11.9", Description = "Type 2 DM", Type = type, ChapterDescription = "Endocrine" }, "R1");
        e.IsBillable.Should().Be(billable);
        e.Code.Should().Be("E11.9");
        e.Chapter.Should().Be("Endocrine");
    }

    [Fact]
    public void Icd_code_is_normalized_upper_trimmed()
        => Mappers.ToIcd(new IcdCsvRow { Code = " e11.9 ", Description = "x", Type = "category" }, "R1").Code.Should().Be("E11.9");

    [Fact]
    public void Cpt_maps_code_description_category()
    {
        var e = Mappers.ToCpt(new CptCsvRow { Code = "0001U", Category = "PLA", Description = "RBC antigen typing" }, "R1");
        e.Code.Should().Be("0001U");
        e.Category.Should().Be("PLA");
        e.Description.Should().Be("RBC antigen typing");
    }

    [Fact]
    public void Drug_code_is_a_stable_normalized_natural_key()
    {
        var a = Mappers.ToDrug(new DrugCsvRow { CommercialNameEn = "1 2 3 (ONE TWO THREE) 20 F.C.TABS." }, "R1");
        var b = Mappers.ToDrug(new DrugCsvRow { CommercialNameEn = "  1 2 3 (one two three) 20 f.c.tabs.  " }, "R1");
        a.DrugCode.Should().Be(b.DrugCode); // case/whitespace-insensitive → dedupes on re-load
        a.DrugCode.Should().NotContain(" ");
    }

    [Theory]
    [InlineData("A", 1)]
    [InlineData("A10", 2)]
    [InlineData("A10B", 3)]
    [InlineData("A10BA", 4)]
    [InlineData("A10BA02", 5)]
    [InlineData("A10BA0X9", 0)] // bad length
    public void Atc_level_derived_from_code_length(string code, int level)
        => MasterDataNormalize.AtcLevel(code).Should().Be(level);

    [Fact]
    public void Atc_classes_derived_from_a_drug_row_cover_all_present_levels()
    {
        var row = new DrugCsvRow
        {
            CommercialNameEn = "GLUCOPHAGE 500", AtcCode = "A10BA02",
            AtcL1 = "Alimentary tract and metabolism", AtcL2 = "Drugs used in diabetes",
            AtcL3 = "Blood glucose lowering drugs", AtcL4 = "Biguanides", AtcL5 = "Metformin",
        };

        var classes = Mappers.ToAtcClasses(row, "R1").ToList();

        classes.Select(c => c.AtcCode).Should().Equal("A", "A10", "A10B", "A10BA", "A10BA02");
        classes.Single(c => c.AtcCode == "A10BA02").Title.Should().Be("Metformin");
        classes.Single(c => c.AtcCode == "A").Level.Should().Be(1);
        classes.Single(c => c.AtcCode == "A10BA02").Level.Should().Be(5);
    }

    [Fact]
    public void Drug_without_atc_yields_no_atc_classes()
        => Mappers.ToAtcClasses(new DrugCsvRow { CommercialNameEn = "X", AtcCode = "" }, "R1").Should().BeEmpty();

    [Fact]
    public void Atc_ancestors_are_truncations()
        => MasterDataNormalize.AtcAncestors("A10BA02").Should().Equal("A", "A10", "A10B", "A10BA");
}
