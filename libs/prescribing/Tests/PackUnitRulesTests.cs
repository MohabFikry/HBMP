using FluentAssertions;
using Mersal.Prescribing;

namespace Mersal.Prescribing.Tests;

/// <summary>
/// 29.6 / design 45 §6 — deriving the prescribing unit and splittability from the dosage form.
///
/// <para>"`is_pack_splittable` DEFAULTS FROM THE DOSAGE FORM but must be OVERRIDABLE per product — the form
/// is a good heuristic and a poor law."</para>
/// </summary>
public class PackUnitRulesTests
{
    [Theory]
    [InlineData("tablet", "Tablet", true)]
    [InlineData("f.c. tablet", "Tablet", true)]
    [InlineData("capsule", "Capsule", true)]
    [InlineData("sachet", "Sachet", true)]
    [InlineData("suppository", "Suppository", true)]
    public void Splittable_forms_are_counted_in_their_own_units(string form, string unit, bool splittable)
    {
        var derived = PackUnitRules.FromDosageForm(form);

        derived.PrescribingUnit.Should().Be(unit);
        derived.IsPackSplittable.Should().Be(splittable);
    }

    [Theory]
    [InlineData("inhaler", "Puff")]
    [InlineData("pre-filled pen", "IU")]
    [InlineData("vial", "Vial")]
    [InlineData("ampoule", "Ampoule")]
    [InlineData("patch", "Patch")]
    [InlineData("spray", "Spray")]
    public void Non_splittable_forms_are_dispensed_whole(string form, string unit)
    {
        // "For non-splittable forms (inhalers, pre-filled pens, vials, sprays) the unit is the whole item."
        var derived = PackUnitRules.FromDosageForm(form);

        derived.PrescribingUnit.Should().Be(unit);
        derived.IsPackSplittable.Should().BeFalse();
    }

    [Fact]
    public void An_unrecognised_form_derives_NOTHING_rather_than_a_plausible_default()
    {
        // The dangerous default is `splittable = true`: it silently permits a fractional inhaler. Absence is
        // carried through as absence, and the quantity check reports NotChecked naming the field.
        var derived = PackUnitRules.FromDosageForm("herbal preparation");

        derived.PrescribingUnit.Should().BeNull();
        derived.IsPackSplittable.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_form_derives_nothing(string? form)
    {
        PackUnitRules.FromDosageForm(form).IsPackSplittable.Should().BeNull();
    }

    [Fact]
    public void A_derived_value_is_only_a_default_and_never_overrides_a_stated_one()
    {
        // "The form is a good heuristic and a poor law." A product-level override always wins — a chewable
        // tablet that must not be halved, or a scored one that may be.
        PackUnitRules.Resolve(form: "tablet", statedSplittable: false).IsPackSplittable.Should().BeFalse();
        PackUnitRules.Resolve(form: "inhaler", statedSplittable: true).IsPackSplittable.Should().BeTrue();
        PackUnitRules.Resolve(form: "tablet", statedSplittable: null).IsPackSplittable.Should().BeTrue();
    }

    // ---- the PACK COLUMNS, which outrank the form -------------------------------------------------------

    [Theory]
    // "Major Units (per box)" = strips/blisters/containers per box; "Minor Units (total)" = the total
    // PRESCRIBING units in the box. Real rows from the workbook, one per shape the data actually takes.
    [InlineData(2, 20, 20, true)]     // 20 f.c. tabs in 2 strips — 20 dispensable units
    [InlineData(1, 10, 10, true)]     // 10 sachets in one box
    [InlineData(6, 60, 60, true)]     // 60 capsules in 6 strips
    [InlineData(3, 3, 3, true)]       // a box of 3 ampoules — three whole items, and one of them may be given
    [InlineData(5, 5, 5, true)]       // 5 x 3ml insulin penfills
    [InlineData(1, 1, 1, false)]      // a 120 ml syrup bottle, a 100 gm tube, ONE inhaler — indivisible
    public void The_pack_columns_decide_splittability(int major, int minor, int packSize, bool splittable)
    {
        // THE RULE, in one sentence: a pack holding more than one prescribing unit can be split; a pack that
        // IS one unit cannot. Derived from the two columns rather than from the dosage form, because the form
        // gets the interesting cases wrong in both directions — it calls a box of three ampoules unsplittable
        // (it is three separate items) and it has nothing to say about a form it does not recognise.
        var derived = PackUnitRules.FromPackUnits(major, minor);

        derived.PackSize.Should().Be(packSize);
        derived.IsPackSplittable.Should().Be(splittable);
    }

    [Theory]
    [InlineData(null, 20, 20, true)]   // 4 workbook rows carry no major count; the minor total still answers
    [InlineData(null, 1, 1, false)]
    public void The_minor_total_alone_is_enough(int? major, int minor, int packSize, bool splittable)
    {
        var derived = PackUnitRules.FromPackUnits(major, minor);

        derived.PackSize.Should().Be(packSize);
        derived.IsPackSplittable.Should().Be(splittable);
    }

    [Theory]
    [InlineData(2, 1)]      // 46 workbook rows: fewer minor units than major containers
    [InlineData(10, 3)]
    public void An_incoherent_pair_derives_NOTHING_rather_than_the_smaller_number(int major, int minor)
    {
        // A box cannot hold fewer prescribing units than the containers it is made of, so one of the two
        // numbers is wrong and there is no way to tell which. Taking either would produce a confident
        // quantity from data known to be broken — invariant 8's exact prohibition.
        var derived = PackUnitRules.FromPackUnits(major, minor);

        derived.PackSize.Should().BeNull();
        derived.IsPackSplittable.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, null)]
    [InlineData(1, 0)]
    [InlineData(1, -3)]
    public void A_missing_or_nonsense_minor_total_derives_nothing(int? major, int? minor)
    {
        var derived = PackUnitRules.FromPackUnits(major, minor);

        derived.PackSize.Should().BeNull();
        derived.IsPackSplittable.Should().BeNull();
    }

    [Fact]
    public void The_pack_columns_outrank_the_dosage_form_but_not_a_product_level_statement()
    {
        // Precedence, most authoritative first: the product's own record, then the pack columns (measured),
        // then the dosage form (a guess from a free-text word).
        //
        // The middle rank is the change. "ampoule" derives NOT splittable, but a box of three ampoules is
        // three separate items and giving one of them is routine — the columns know that and the form does
        // not.
        PackUnitRules.Resolve("ampoule", statedSplittable: null, majorUnits: 3, minorUnits: 3)
            .IsPackSplittable.Should().BeTrue();

        // And where the columns say nothing, the form is still consulted rather than the answer being lost.
        PackUnitRules.Resolve("tablet", statedSplittable: null, majorUnits: null, minorUnits: null)
            .IsPackSplittable.Should().BeTrue();

        // A stated product-level value still wins over both — a chewable tablet that must not be halved.
        PackUnitRules.Resolve("tablet", statedSplittable: false, majorUnits: 2, minorUnits: 20)
            .IsPackSplittable.Should().BeFalse();
    }

    [Theory]
    // The forms that carried 2,495 real products with NO derivable unit, so every one of them reported
    // NotChecked and could not be written as a chronic script at all.
    [InlineData("lozenge", "Lozenge")]
    [InlineData("pessary", "Pessary")]
    [InlineData("prefilled syringe", "Syringe")]
    [InlineData("cartridge", "Cartridge")]
    [InlineData("lotion", "ML")]
    [InlineData("shampoo", "ML")]
    [InlineData("mouth wash", "ML")]
    [InlineData("oral liquid", "ML")]
    [InlineData("elixir", "ML")]
    [InlineData("emulsion", "ML")]
    [InlineData("oil", "ML")]
    [InlineData("granules", "Gram")]
    [InlineData("paste", "Gram")]
    [InlineData("powder", "Gram")]
    [InlineData("gummy", "Gummy")]
    [InlineData("soap", "Bar")]
    [InlineData("enema", "Enema")]
    [InlineData("dressing", "Dressing")]
    public void The_units_the_catalogue_actually_uses_are_all_recognised(string form, string unit)
    {
        // The UNIT is a label — what the dose field is counted in. Splittability, the fact a wrong answer
        // could actually hurt someone, no longer comes from here at all.
        PackUnitRules.FromDosageForm(form).PrescribingUnit.Should().Be(unit);
    }

    [Theory]
    // The vocabulary is CLOSED and enforced by a CHECK constraint, so it grows only where the catalogue
    // genuinely needs a word it does not have. These two do not: a vaccine is supplied AS a vial, and a
    // herbal "bag" is a tea bag, which is a sachet.
    [InlineData("vaccine", "Vial")]
    [InlineData("herbal bag", "Sachet")]
    public void A_form_that_an_existing_unit_already_describes_does_not_get_a_new_word(string form, string unit)
    {
        PackUnitRules.FromDosageForm(form).PrescribingUnit.Should().Be(unit);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("topical")]
    [InlineData("vaginal")]
    [InlineData("mask")]
    [InlineData("sheet")]
    public void A_form_that_names_no_unit_still_derives_nothing(string form)
    {
        // These name a ROUTE or a shape, not a countable unit. Guessing one would put a word in front of the
        // prescriber that reads as data.
        PackUnitRules.FromDosageForm(form).PrescribingUnit.Should().BeNull();
    }

    [Fact]
    public void Unit_data_is_incomplete_until_all_three_facts_are_known()
    {
        // The flag drives `NotChecked`, so it must be true whenever ANY of the three is missing — a row with a
        // unit and a splittability but no pack CONTENT still cannot be converted to boxes. Content, not pack
        // size: a 120 ml syrup bottle is `pack_size = 1`, which looked complete and was not (31.3).
        PackUnitRules.IsComplete(unit: "Tablet", packContent: 20m, splittable: true).Should().BeTrue();
        PackUnitRules.IsComplete(unit: null, packContent: 20m, splittable: true).Should().BeFalse();
        PackUnitRules.IsComplete(unit: "Tablet", packContent: null, splittable: true).Should().BeFalse();
        PackUnitRules.IsComplete(unit: "Tablet", packContent: 20m, splittable: null).Should().BeFalse();
        PackUnitRules.IsComplete(unit: "Tablet", packContent: 0m, splittable: true).Should().BeFalse();
    }
}
