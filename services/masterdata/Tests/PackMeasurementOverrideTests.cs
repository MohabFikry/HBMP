using FluentAssertions;
using Mersal.MasterData.Loader;

namespace Mersal.MasterData.Tests;

/// <summary>
/// 31.3 — the measurements the workbook does not carry, supplied by name and with a reason.
///
/// ============================================================================================================
/// WHY THIS EXISTS AND WHY IT IS NOT A RULE
/// ============================================================================================================
/// "Lantus Solostar 100 I.U./ML 5 Pens" states its concentration and never how many millilitres a pen holds,
/// so the box's contents in IU are underivable and no box count can be offered. Three millilitres is the
/// standard fill of every marketed insulin pen and cartridge — but <b>encoding that as a rule in code</b> is
/// the guess invariant 8 forbids: it would silently apply to the next product that is not 3 ml, and the wrong
/// box count would look exactly like a right one.
///
/// So the facts are supplied as DATA, one line per product, each carrying its own basis. That has three
/// properties a rule does not:
///
///   * it is reviewable — a pharmacist can read the file and check twenty-six values;
///   * it cannot spread — a new insulin appearing in a workbook refresh gets no volume, so it shows up in the
///     loader's missing-content report rather than quietly inheriting somebody's assumption;
///   * it is subordinate to the source — the workbook wins wherever the workbook speaks.
///
/// The last is what these tests mostly assert. An override that could overwrite the catalogue would be a
/// second, invisible answer to what is in a box.
/// </summary>
public class PackMeasurementOverrideTests
{
    private static PackMeasurementOverrides Two() => PackMeasurementOverrides.From([
        new PackMeasurementOverrideRow
        {
            SourceRowId = "7101", TradeName = "Lantus Solostar", VolumeMl = "3", Basis = "SoloStar pen — 3 ml",
        },
        new PackMeasurementOverrideRow
        {
            SourceRowId = "31633", TradeName = "Xultophy", IuPerMl = "100", Basis = "combination pen",
        },
    ]);

    [Fact]
    public void A_row_with_no_volume_in_the_workbook_takes_the_stated_one()
    {
        var facts = Mappers.PackFactsOf(
            new DrugListXlsxRow
            {
                SourceRowId = "7101", TradeNameEn = "lantus solostar 100 i.u./ml 5 pens",
                DosageForm = "prefilled pen", MajorUnits = "5", MinorUnits = "5", Strength = "100iu/ml",
            },
            Two());

        facts.PrescribingUnit.Should().Be("IU");
        facts.PackContent.Should().Be(1500m, "five pens of 3 ml at 100 IU/ml");
    }

    [Fact]
    public void The_WORKBOOK_wins_wherever_it_speaks()
    {
        // Toujeo's own cell says 1.5 ml. An override claiming 3 must not quietly double every box.
        var facts = Mappers.PackFactsOf(
            new DrugListXlsxRow
            {
                SourceRowId = "7101", TradeNameEn = "toujeo solostar 300 i.u./ml 1.5 ml 3 pens",
                DosageForm = "prefilled pen", MajorUnits = "3", MinorUnits = "3",
                VolumeWeight = "1.5 ml", Strength = "300 iu",
            },
            Two());

        facts.PackContent.Should().Be(1350m, "3 pens x 1.5 ml x 300 IU/ml — the sheet's own volume");
    }

    [Fact]
    public void A_concentration_may_be_supplied_where_a_combination_product_spells_it_unreadably()
    {
        // "100 i.u./3.6 MG/ML" states 100 IU and 3.6 mg per millilitre. No parser reads that without also
        // mis-reading something else, and the volume is already in the name.
        var facts = Mappers.PackFactsOf(
            new DrugListXlsxRow
            {
                SourceRowId = "31633", TradeNameEn = "xultophy 100 i.u./3.6 mg/ml prefilled pen 3 ml",
                DosageForm = "prefilled pen", MajorUnits = "1", MinorUnits = "1", Strength = "100 iu/3.6mg",
            },
            Two());

        facts.PrescribingUnit.Should().Be("IU");
        facts.PackContent.Should().Be(300m, "one pen of 3 ml at 100 IU/ml");
    }

    [Fact]
    public void A_supplied_concentration_reaches_a_row_the_sheet_answered_WRONGLY()
    {
        // "insulin h bio nph 100i.u.vial" — the Strength cell says "100 iu" and drops the "/ml" that every
        // other insulin row in the sheet carries. So the sheet DID produce an answer: unit Vial, content one
        // vial. It is a coherent answer to the wrong question — nobody doses insulin in vials — and gating
        // the override on "did the sheet produce a content" would have skipped this row entirely.
        //
        // The precedence is per INPUT, not per outcome: the sheet's concentration is missing, so the stated
        // one is used, and the unit becomes IU as it does for every other insulin.
        var overrides = PackMeasurementOverrides.From([
            new PackMeasurementOverrideRow
            {
                SourceRowId = "20863", TradeName = "Insulin H Bio Nph", VolumeMl = "10", IuPerMl = "100",
                Basis = "U-100 vial",
            },
        ]);

        var facts = Mappers.PackFactsOf(
            new DrugListXlsxRow
            {
                SourceRowId = "20863", TradeNameEn = "insulin h bio nph 100i.u.vial",
                DosageForm = "vial", MajorUnits = "1", MinorUnits = "1", Strength = "100 iu",
            },
            overrides);

        facts.PrescribingUnit.Should().Be("IU", "insulin is dosed in IU whatever holds it");
        facts.PackContent.Should().Be(1000m, "one 10 ml vial at 100 IU/ml");
        overrides.Unused().Should().BeEmpty();
    }

    [Fact]
    public void A_product_with_no_override_derives_nothing_and_says_nothing()
    {
        // The whole point of a list rather than a rule: absence stays absent, and stays visible in the report.
        var facts = Mappers.PackFactsOf(
            new DrugListXlsxRow
            {
                SourceRowId = "20878", TradeNameEn = "insunil h nph 100iu/ml vial",
                DosageForm = "vial", MajorUnits = "1", MinorUnits = "1", Strength = "100 iu/ml",
            },
            Two());

        facts.PrescribingUnit.Should().Be("IU");
        facts.PackContent.Should().BeNull();
    }

    [Fact]
    public void An_entry_that_matched_nothing_is_REPORTED_rather_than_ignored()
    {
        // A row id that no longer exists means the workbook moved on and this file did not. Silence there is
        // how a curated list rots into a list of things that used to be true.
        var overrides = PackMeasurementOverrides.From([
            new PackMeasurementOverrideRow { SourceRowId = "999999", TradeName = "gone", VolumeMl = "3", Basis = "x" },
        ]);

        Mappers.PackFactsOf(
            new DrugListXlsxRow { SourceRowId = "7101", TradeNameEn = "lantus", DosageForm = "prefilled pen" },
            overrides);

        overrides.Unused().Should().ContainSingle().Which.SourceRowId.Should().Be("999999");
    }

    [Fact]
    public void The_shipped_file_parses_and_every_entry_states_a_basis()
    {
        // Read from disk, so a typo in the CSV fails here rather than at 2am against a real catalogue.
        var overrides = PackMeasurementOverrides.Load(PackMeasurementOverrides.DefaultPath);

        overrides.Count.Should().BeGreaterThan(20);
        foreach (var row in overrides.All)
        {
            row.Basis.Should().NotBeNullOrWhiteSpace($"'{row.TradeName}' supplies a measurement with no reason");
            (row.VolumeMl is not null || row.IuPerMl is not null).Should()
                .BeTrue($"'{row.TradeName}' overrides nothing");
        }
    }
}
