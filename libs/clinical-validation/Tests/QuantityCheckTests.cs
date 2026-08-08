using FluentAssertions;
using Xunit;

namespace Mersal.ClinicalValidation.Tests;

/// <summary>
/// 29.6 (design 45 §6, invariant 8) — <b>the quantity check the PRESCRIBER sees.</b>
///
/// <para>The rule "missing unit data yields NotChecked NAMING the missing field, never a guessed quantity"
/// was enforced on the write path — pharmacy refuses 422 — and nowhere the prescriber could see it. So a
/// doctor composing a script against a drug whose pack facts are absent got a clean-looking panel and a
/// refusal only at submit, with no field named and nothing to act on.</para>
///
/// <para><b>Why NotChecked and not a warning.</b> This platform's rule is that absence of data is never a
/// clean result and never a bad one either. A warning would say "this quantity is suspect"; the truth is
/// "this quantity could not be computed", and the difference decides whether the prescriber corrects a
/// number or corrects the master data.</para>
/// </summary>
public class QuantityCheckTests
{
    private static Finding Quantity(ValidationResult r, Guid lineId) => r.For(lineId, CheckKind.Quantity);

    [Fact]
    public void A_splittable_pack_computes_the_total_and_reports_Ok()
    {
        // 1 tablet three times daily for 30 days = 90 tablets. Tablets split from a pack, so 90 stands.
        var line = Fx.Line(doseAmount: 1, doseUnit: "tablet", timesPerDay: 3, durationDays: 30);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(packFacts: Fx.PackFacts((line.DrugId, isSplittable: true, packSize: 20m))));

        Quantity(result, line.LineId).State.Should().Be(CheckState.Ok);
        Quantity(result, line.LineId).MessageEn.Should().Contain("90");
    }

    [Fact]
    public void A_NON_splittable_pack_rounds_UP_to_whole_items()
    {
        // 200 puffs needed, 100 puffs per canister that cannot be broken -> 2 canisters. Rounding DOWN
        // would send a patient home short; rounding at all is only legitimate because the pack is the
        // dispensable unit.
        var line = Fx.Line(doseAmount: 2, doseUnit: "puff", timesPerDay: 2, durationDays: 50);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(packFacts: Fx.PackFacts((line.DrugId, isSplittable: false, packSize: 100m))));

        Quantity(result, line.LineId).State.Should().Be(CheckState.Ok);
        Quantity(result, line.LineId).MessageEn.Should().Contain("200");
    }

    [Fact]
    public void Unknown_splittability_yields_NotChecked_NAMING_the_field()
    {
        // THE INVARIANT. Assuming splittable is the dangerous default — it permits a fractional inhaler.
        var line = Fx.Line(doseAmount: 1, doseUnit: "tablet", timesPerDay: 1, durationDays: 30);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(packFacts: Fx.PackFacts((line.DrugId, isSplittable: null, packSize: 20m))));

        Quantity(result, line.LineId).State.Should().Be(CheckState.NotChecked);
        Quantity(result, line.LineId).MessageEn.Should().Contain("is_pack_splittable",
            "the missing field is named as the master-data COLUMN — the vocabulary of the person who fixes it");
    }

    [Fact]
    public void A_missing_pack_size_yields_NotChecked_NAMING_the_field()
    {
        var line = Fx.Line(doseAmount: 1, doseUnit: "puff", timesPerDay: 2, durationDays: 30);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(packFacts: Fx.PackFacts((line.DrugId, isSplittable: false, packSize: null))));

        Quantity(result, line.LineId).State.Should().Be(CheckState.NotChecked);
        Quantity(result, line.LineId).MessageEn.Should().Contain("pack_size");
    }

    [Fact]
    public void A_drug_absent_from_the_pack_table_is_NotChecked_rather_than_assumed()
    {
        // A drug with no row at all is not a drug with tidy defaults. 2,495 real rows are in this state.
        var line = Fx.Line(doseAmount: 1, doseUnit: "tablet", timesPerDay: 1, durationDays: 30);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(packFacts: Fx.PackFacts()));

        Quantity(result, line.LineId).State.Should().Be(CheckState.NotChecked);
    }

    [Fact]
    public void The_check_reports_Unavailable_when_master_data_could_not_be_reached()
    {
        // FIVE states, never four: "the source is down" is not "the data is missing", and a prescriber
        // deciding whether to chase a data fix needs to know which.
        var line = Fx.Line(doseAmount: 1, doseUnit: "tablet", timesPerDay: 1, durationDays: 30);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(packFacts: Fetched.NotAvailable<IReadOnlyDictionary<Guid, DrugPackFacts>>("masterdata unreachable")));

        Quantity(result, line.LineId).State.Should().Be(CheckState.Unavailable);
    }

    [Fact]
    public void The_check_never_BLOCKS_a_prescription()
    {
        // Design 43's rule, unchanged: benefit rules may block, clinical checks may only warn. A quantity
        // that cannot be computed is a data problem, and refusing to let a doctor prescribe over one would
        // make a missing spreadsheet column into a clinical stop.
        var line = Fx.Line(doseAmount: 1, doseUnit: "tablet", timesPerDay: 1, durationDays: 30);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(packFacts: Fx.PackFacts((line.DrugId, isSplittable: null, packSize: null))));

        Quantity(result, line.LineId).IsBlocking.Should().BeFalse();
        Quantity(result, line.LineId).RequiresAcknowledgement.Should().BeFalse(
            "an uncomputable quantity is not an override a prescriber can justify — it is a fact nobody has");
    }

    [Fact]
    public void Without_a_dose_or_a_duration_there_is_nothing_to_compute_and_it_says_so()
    {
        // The commonest case on a free-text dose. NotChecked naming what is absent, not a silent Ok.
        var line = Fx.Line(doseAmount: null, timesPerDay: null, durationDays: null);

        var result = Fx.Run(
            Fx.Request([line]),
            Fx.Snapshot(packFacts: Fx.PackFacts((line.DrugId, isSplittable: true, packSize: 20m))));

        Quantity(result, line.LineId).State.Should().Be(CheckState.NotChecked);
    }
}
