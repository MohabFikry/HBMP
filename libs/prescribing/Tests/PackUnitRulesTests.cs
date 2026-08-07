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

    [Fact]
    public void Unit_data_is_incomplete_until_all_three_facts_are_known()
    {
        // The flag drives `NotChecked`, so it must be true whenever ANY of the three is missing — a row with a
        // unit and a splittability but no pack size still cannot be converted to whole packs.
        PackUnitRules.IsComplete(unit: "Tablet", packSize: 20m, splittable: true).Should().BeTrue();
        PackUnitRules.IsComplete(unit: null, packSize: 20m, splittable: true).Should().BeFalse();
        PackUnitRules.IsComplete(unit: "Tablet", packSize: null, splittable: true).Should().BeFalse();
        PackUnitRules.IsComplete(unit: "Tablet", packSize: 20m, splittable: null).Should().BeFalse();
        PackUnitRules.IsComplete(unit: "Tablet", packSize: 0m, splittable: true).Should().BeFalse();
    }
}
