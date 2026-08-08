using FluentAssertions;
using Mersal.Prescribing;

namespace Mersal.Prescribing.Tests;

/// <summary>
/// 31.3 — HOW MUCH IS IN A BOX, in the unit the medicine is dosed in (design 45 §6).
///
/// <para><b>The defect these pin.</b> The catalogue's two unit columns count containers, not doses. A box of
/// five insulin pens is <c>minor = 5</c>; a 120 ml bottle of syrup is <c>minor = 1</c>. Dividing a course
/// total by that number answers a question nobody asked: 210 ml of syrup came out as <b>210 bottles</b>, and
/// 2250 IU of insulin came out as "boxes cannot be counted for this product".</para>
///
/// <para><b>Where the missing number actually lives.</b> In two columns the loader was reading past —
/// "Volume / Weight" and "Strength". A container's contents are its volume; for a product measured in IU they
/// are its volume times its concentration. Multiply by "Major Units (per box)", which is the CONTAINER count
/// (1 for a 10 ml vial, 5 for five penfills, 3 for three pens), and the box's contents follow.</para>
///
/// <para><b>Major, not minor, for the measured forms.</b> The minor column is not a container count and does
/// not pretend to be: "actrapid hm 100 i.u./ml 10 ml vial" carries <c>major = 1, minor = 10</c> — one vial,
/// ten millilitres. Multiplying the per-container volume by the minor column would make that box hold 100 ml.
/// </para>
/// </summary>
public class PackContentTests
{
    // ---------------------------------------------------------------- the measurement parsers

    [Theory]
    [InlineData("120 ml", 120.0)]
    [InlineData("1.5 ml", 1.5)]
    [InlineData("10ml", 10.0)]
    [InlineData("2.5 ML", 2.5)]
    [InlineData("1 litre", 1000.0)]
    [InlineData("30 gm", null)]     // a weight is not a volume
    [InlineData("10*10 cm", null)]  // a dressing measured in centimetres
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Millilitres_reads_a_volume_and_nothing_else(string? text, double? expected)
    {
        PackMeasure.Millilitres(text).Should().Be((decimal?)expected);
    }

    [Theory]
    [InlineData("30 gm", 30.0)]
    [InlineData("100 g", 100.0)]
    [InlineData("1 kg", 1000.0)]
    [InlineData("120 ml", null)]
    [InlineData(null, null)]
    public void Grams_reads_a_weight_and_nothing_else(string? text, double? expected)
    {
        PackMeasure.Grams(text).Should().Be((decimal?)expected);
    }

    [Theory]
    // The workbook writes this half a dozen ways, and all of them mean the same concentration.
    [InlineData("100 iu/ml", 100.0)]
    [InlineData("100iu/ml", 100.0)]
    [InlineData("100 i.u./ml", 100.0)]
    [InlineData("300 I.U./ML", 300.0)]
    [InlineData("10000i.u./ml", 10000.0)]
    // A TOTAL is not a concentration. "50000 iu" is what one capsule of vitamin D holds; reading it as a
    // concentration would multiply it by a volume and hand out a hundred times the course.
    [InlineData("50000 iu", null)]
    [InlineData("500 mg", null)]
    [InlineData(null, null)]
    public void IuPerMillilitre_reads_a_CONCENTRATION_only(string? text, double? expected)
    {
        PackMeasure.IuPerMillilitre(text).Should().Be((decimal?)expected);
    }

    // ---------------------------------------------------------------- what a box holds

    [Fact]
    public void A_countable_form_holds_its_minor_units()
    {
        // 24 tablets in a box, whatever the strips. Nothing to measure — the box IS a count.
        var facts = PackUnitRules.Resolve(
            form: "f.c. tablet", statedSplittable: null, majorUnits: 2m, minorUnits: 24m);

        facts.PrescribingUnit.Should().Be("Tablet");
        facts.PackContent.Should().Be(24m);
    }

    [Fact]
    public void A_syrup_bottle_holds_its_millilitres()
    {
        // THE DANGEROUS ONE. `minor = 1` counts the bottle, and a 210 ml course divided by it reported 210
        // bottles — a number the prescriber would have had to catch by eye.
        var facts = PackUnitRules.Resolve(
            form: "syrup", statedSplittable: null, majorUnits: 1m, minorUnits: 1m, volumeWeight: "120 ml");

        facts.PrescribingUnit.Should().Be("ML");
        facts.PackContent.Should().Be(120m);
    }

    [Fact]
    public void A_tube_of_cream_holds_its_grams()
    {
        var facts = PackUnitRules.Resolve(
            form: "cream", statedSplittable: null, majorUnits: 1m, minorUnits: 1m, volumeWeight: "30 gm");

        facts.PrescribingUnit.Should().Be("Gram");
        facts.PackContent.Should().Be(30m);
    }

    [Fact]
    public void A_box_of_ampoules_holds_its_ampoules()
    {
        // "calcitonium 50 i.u. 5 amps" — five ampoules, and both columns say five.
        var facts = PackUnitRules.Resolve(
            form: "ampoule", statedSplittable: null, majorUnits: 5m, minorUnits: 5m, volumeWeight: "1 ml");

        facts.PrescribingUnit.Should().Be("Ampoule");
        facts.PackContent.Should().Be(5m);
    }

    [Fact]
    public void Two_columns_that_contradict_each_other_derive_NO_content()
    {
        // "adwiflam 75mg/3ml 6 amp" carries major = 6 and minor = 60. Six ampoules and sixty of something
        // cannot both be true, nothing in the row says which is the mistake, and the gap between them is the
        // gap between one box and ten. 106 container rows are in this state.
        var facts = PackUnitRules.Resolve(
            form: "ampoule", statedSplittable: null, majorUnits: 6m, minorUnits: 60m,
            volumeWeight: "3 ml", strength: "75mg/3ml");

        facts.PackContent.Should().BeNull();
    }

    [Fact]
    public void A_product_stated_in_IU_per_ml_is_dosed_in_IU()
    {
        // "actrapid hm 100 i.u./ml 5*3ml penfills" — five cartridges, 3 ml each, 100 IU per ml = 1500 IU.
        var facts = PackUnitRules.Resolve(
            form: "cartridge", statedSplittable: null, majorUnits: 5m, minorUnits: 5m,
            volumeWeight: "3 ml", strength: "100 iu/ml");

        facts.PrescribingUnit.Should().Be("IU", "the container is a cartridge; the medicine is counted in IU");
        facts.PackContent.Should().Be(1500m);
    }

    [Fact]
    public void A_measured_product_takes_its_container_count_from_the_MAJOR_column_alone()
    {
        // "actrapid hm 100 i.u./ml 10 ml vial" carries major = 1 and minor = 10. That is not a contradiction
        // about how many vials are in the box — it is one vial, and the minor column is counting its
        // MILLILITRES. Requiring the two to agree here would throw away a row whose contents are perfectly
        // derivable: 1 × 10 ml × 100 IU/ml = 1000 IU.
        //
        // The same disagreement on an AMPOULE means something different, and is refused — see below.
        var facts = PackUnitRules.Resolve(
            form: "vial", statedSplittable: null, majorUnits: 1m, minorUnits: 10m,
            volumeWeight: "10 ml", strength: "100iu/ml");

        facts.PrescribingUnit.Should().Be("IU");
        facts.PackContent.Should().Be(1000m);
    }

    [Fact]
    public void The_concentration_may_come_from_the_trade_name_when_the_strength_column_is_loose()
    {
        // "toujeo solostar 300 i.u./ml 1.5 ml 3 pens" — the Strength cell says "300 iu", dropping the "/ml"
        // that the NAME states. Both are the same fact and only one of them is written properly.
        var facts = PackUnitRules.Resolve(
            form: "prefilled pen", statedSplittable: null, majorUnits: 3m, minorUnits: 3m,
            volumeWeight: "1.5 ml", strength: "300 iu",
            tradeName: "toujeo solostar 300 i.u./ml 1.5 ml 3 pens");

        facts.PrescribingUnit.Should().Be("IU");
        facts.PackContent.Should().Be(1350m);
    }

    [Fact]
    public void An_IU_product_with_no_volume_anywhere_derives_NO_content()
    {
        // "lantus solostar 100 i.u./ml 5 pens" — the workbook records no volume for it, in either column or
        // in the name. Three millilitres is the usual fill of an insulin pen and it is NOT assumed here:
        // a guessed pack size is a guessed box count, and invariant 8 exists because that error is invisible.
        var facts = PackUnitRules.Resolve(
            form: "prefilled pen", statedSplittable: null, majorUnits: 5m, minorUnits: 5m,
            strength: "100iu/ml", tradeName: "lantus solostar 100 i.u./ml 5 pens");

        facts.PrescribingUnit.Should().Be("IU");
        facts.PackContent.Should().BeNull();
    }

    [Fact]
    public void A_capsule_stated_as_a_TOTAL_in_IU_stays_a_capsule()
    {
        // "a-viton 50.000 i.u. 20 caps" — 50,000 IU is what one capsule holds, not a concentration. It is
        // prescribed as capsules, and reading the number as IU per millilitre would be an order of magnitude
        // of nonsense.
        var facts = PackUnitRules.Resolve(
            form: "capsule", statedSplittable: null, majorUnits: 2m, minorUnits: 20m, strength: "50000 iu");

        facts.PrescribingUnit.Should().Be("Capsule");
        facts.PackContent.Should().Be(20m);
    }

    // ---------------------------------------------------------------- the units a prescriber says

    [Theory]
    // A nasal spray delivers SPRAYS; an oral or sublingual one delivers PUFFS. Both were "Spray", which is
    // the word for only one of them.
    [InlineData("nasal spray", "Spray")]
    [InlineData("spray", "Spray")]
    [InlineData("oral spray", "Puff")]
    [InlineData("sublingual spray", "Puff")]
    [InlineData("inhaler", "Puff")]
    public void The_unit_names_what_the_device_actually_delivers(string form, string unit)
    {
        PackUnitRules.FromDosageForm(form).PrescribingUnit.Should().Be(unit);
    }

    [Theory]
    [InlineData("Tablet", "tabs")]
    [InlineData("Capsule", "caps")]
    [InlineData("IU", "IU")]
    [InlineData("Spray", "sprays")]
    [InlineData("Puff", "puffs")]
    [InlineData("ML", "ml")]
    [InlineData("Gram", "gm")]
    [InlineData("Ampoule", "amps")]
    public void Every_unit_has_the_short_form_a_prescriber_writes(string unit, string shortForm)
    {
        // The dose field is labelled with this. "Dose (tabs)" is how it is written on a paper prescription;
        // "Dose (Tablet)" is how a database column is named.
        PackUnitRules.ShortUnit(unit).Should().Be(shortForm);
    }

    [Fact]
    public void Every_unit_in_the_vocabulary_has_a_short_form()
    {
        // A unit added to the closed vocabulary without one would render the dose field labelled with the
        // database's word for it, on one drug in the catalogue, and nobody would see it until a prescriber did.
        foreach (var unit in PackUnitRules.Units)
            PackUnitRules.ShortUnit(unit).Should().NotBeNullOrWhiteSpace($"'{unit}' has no short form");
    }
}
