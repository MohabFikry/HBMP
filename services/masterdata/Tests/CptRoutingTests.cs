using FluentAssertions;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Tests;

/// <summary>
/// 29.2 / design 45 §2 — the doctor picks a service, the SYSTEM decides the vehicle.
///
/// <para>The distinction under test is not cosmetic: a <b>referral</b> needs its loop closed with a report
/// back and a <b>procedure</b> needs fulfilment and consumption. Routing an E/M code to a procedure order
/// creates a referral that nobody ever closes — the classic outpatient patient-safety failure (design 45
/// §2b), and invariant 3.</para>
/// </summary>
public class CptRoutingTests
{
    [Theory]
    // Surgery — 10004–69990.
    [InlineData("10004", OrderableVehicle.ProcedureOrder)]
    [InlineData("29881", OrderableVehicle.ProcedureOrder)]  // knee arthroscopy
    [InlineData("69990", OrderableVehicle.ProcedureOrder)]
    // Medicine — injections, infusions, dialysis, PHYSIOTHERAPY.
    [InlineData("90281", OrderableVehicle.ProcedureOrder)]
    [InlineData("97110", OrderableVehicle.ProcedureOrder)]  // therapeutic exercise — physiotherapy
    [InlineData("90935", OrderableVehicle.ProcedureOrder)]  // haemodialysis
    [InlineData("96365", OrderableVehicle.ProcedureOrder)]  // IV infusion
    // Radiology — keeps its existing tab.
    [InlineData("70010", OrderableVehicle.RadiologyOrder)]
    [InlineData("71046", OrderableVehicle.RadiologyOrder)]  // chest x-ray
    [InlineData("79999", OrderableVehicle.RadiologyOrder)]
    // Pathology & Laboratory — keeps its existing tab.
    [InlineData("80048", OrderableVehicle.LabOrder)]
    [InlineData("85025", OrderableVehicle.LabOrder)]        // CBC
    [InlineData("88305", OrderableVehicle.LabOrder)]        // surgical pathology
    [InlineData("89398", OrderableVehicle.LabOrder)]
    public void A_code_routes_to_the_vehicle_its_section_creates(string code, OrderableVehicle expected)
    {
        CptRouting.For(code).Vehicle.Should().Be(expected);
    }

    [Theory]
    [InlineData("99202")]   // office visit, new patient
    [InlineData("99213")]   // office visit, established patient
    [InlineData("99499")]
    public void An_evaluation_and_management_code_creates_a_REFERRAL_not_a_procedure(string code)
    {
        // Invariant 3 (design 45 §8). This is the single easiest thing in Gate 2 to get backwards, and the
        // consequence is not a wrong label: a Procedure order is fulfilled and consumed and then finished,
        // whereas a Referral is not done until a report comes back. Route E/M to a procedure and the loop is
        // never opened, so it can never be found open.
        var decision = CptRouting.For(code);

        decision.Vehicle.Should().Be(OrderableVehicle.Referral);
        decision.IsOrderable.Should().BeTrue("E/M IS orderable — it simply creates a referral");
        CptRouting.OrderTypeFor(decision.Vehicle).Should().BeNull("a referral is not an investigation order");
    }

    [Fact]
    public void The_medicine_and_em_overlap_in_the_published_ranges_resolves_to_referral()
    {
        // Design 45 §2 lists Medicine as 90281–99607 and E/M as 99202–99499 — overlapping ranges. Read
        // literally, 99213 is both. The carve-out must win, and it must win in this direction.
        CptRouting.For("99213").Vehicle.Should().Be(OrderableVehicle.Referral);
        CptRouting.For("99070").Vehicle.Should().Be(OrderableVehicle.ProcedureOrder, "below the E/M carve-out");
        CptRouting.For("99500").Vehicle.Should().Be(OrderableVehicle.ProcedureOrder, "above the E/M carve-out");
    }

    [Theory]
    [InlineData("00100")]   // anesthesia
    [InlineData("01999")]
    [InlineData("0001F")]   // Category II performance measure
    [InlineData("0075T")]   // Category III emerging technology
    [InlineData("0016M")]   // MAAA
    public void A_non_orderable_code_says_WHY_rather_than_simply_being_absent(string code)
    {
        // "Absence of data is never a clean result." A code missing from a picker with no explanation is
        // indistinguishable from a catalogue gap, and a doctor who cannot find a service they know exists
        // will assume the system is broken — or worse, order something adjacent.
        var decision = CptRouting.For(code);

        decision.IsOrderable.Should().BeFalse();
        decision.ReasonEn.Should().NotBeNullOrWhiteSpace();
        decision.ReasonAr.Should().NotBeNullOrWhiteSpace("every refusal is bilingual");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-code")]
    public void An_unrecognised_code_is_never_guessed_into_a_clinical_queue(string? code)
    {
        // Fail closed. Deriving a section from arbitrary digits would put an unknown code in a real worklist
        // on the strength of its shape.
        var decision = CptRouting.For(code);

        decision.IsOrderable.Should().BeFalse();
        decision.Vehicle.Should().Be(OrderableVehicle.NotOrderable);
    }

    [Fact]
    public void The_reconciliation_reports_that_category_is_not_the_section()
    {
        // The finding that matters most, because it invalidates the premise rather than qualifying it: the
        // build prompt says to build the routing map from the loaded `category` values. Those values are the
        // CPT TAXONOMY — Category I/II/III/PLA/MAAA — which says how a code was adopted into the book, not
        // whether it is a scan or a blood test. Routing on it would send a chest x-ray and a hysterectomy
        // down one identical path. Reported rather than silently resolved (design 45 §2).
        var loaded = new (string, string?)[]
        {
            ("71046", "Category I"), ("85025", "Category I"), ("99213", "Category I"),
            ("0001F", "Category II"), ("0075T", "Category III"), ("0016M", "MAAA"),
        };

        var report = CptRoutingReconciliation.Build(loaded);

        report.Discrepancies.Should().Contain(d => d.Kind == "category-is-not-the-section");
        report.LoadedCategoryValues.Keys.Should().Contain("Category I");
    }

    [Fact]
    public void The_reconciliation_reports_the_overlapping_published_ranges()
    {
        var report = CptRoutingReconciliation.Build([("99213", "Category I")]);

        report.Discrepancies.Should().Contain(d => d.Kind == "published-ranges-overlap");
    }

    [Fact]
    public void The_reconciliation_reports_sections_the_published_table_omits()
    {
        // Design 45 §2 says "every remaining category is orderable, nothing excluded" and then lists a table
        // with no Anesthesia row. Both cannot be true; the report says so out loud.
        var report = CptRoutingReconciliation.Build([("00100", "Category I"), ("0001F", "Category II")]);

        report.Discrepancies.Should().Contain(d => d.Kind == "section-absent-from-published-ranges");
        report.Discrepancies.Should().Contain(d => d.Kind == "letter-suffixed-codes-outside-the-sectioned-book");
    }

    [Fact]
    public void Every_discrepancy_states_a_resolution_not_just_a_complaint()
    {
        // A reconciliation report that only lists disagreements gets skimmed once and never again. Each
        // finding has to say what the platform DID, because that is the part a reader needs.
        var report = CptRoutingReconciliation.Build(
            [("00100", "Category I"), ("99213", "Category I"), ("0001F", "Category II"), ("10001", "Category I")]);

        report.Discrepancies.Should().NotBeEmpty();
        report.Discrepancies.Should().OnlyContain(d =>
            !string.IsNullOrWhiteSpace(d.Detail) && !string.IsNullOrWhiteSpace(d.Resolution));
        CptRoutingReconciliation.Format(report).Should().Contain("resolution:");
    }
}
