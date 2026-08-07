using FluentAssertions;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Tests;

/// <summary>
/// 28.9 — drug–disease contraindications (design 44 §5).
///
/// <para>
/// The check the original request actually wanted, and the one phase 26 did not build. "Compatibility of the
/// medication with diagnosis" hides two questions: indication mismatch (off-label — legitimate, common, and
/// dismissed constantly) and contraindication (harm). Conflating them is why the first is noise.
/// </para>
/// </summary>
public class ContraindicationMatcherTests
{
    private static DrugComposition Drug(string? atc = null, params string[] ingredients) =>
        new(Guid.NewGuid(),
            ingredients.ToHashSet(StringComparer.Ordinal),
            atc is null ? new HashSet<string>(StringComparer.Ordinal)
                : MasterDataNormalize.AtcAncestors(atc).Append(atc).ToHashSet(StringComparer.Ordinal));

    private static DrugDiseaseContraindication Rule(
        RuleSubjectKind kind, string subject, string scope,
        InteractionSeverity severity = InteractionSeverity.Contraindicated) =>
        new()
        {
            RuleId = Guid.NewGuid(),
            SubjectKind = kind, SubjectValue = subject, IcdScope = scope, Severity = severity,
            MechanismEn = "mechanism", MechanismAr = "آلية",
            ClinicalEffectEn = "effect", ClinicalEffectAr = "أثر",
            ManagementEn = "use paracetamol instead", ManagementAr = "يُستخدم الباراسيتامول",
            EvidenceLevel = EvidenceLevel.Established, Citation = "NICE CG184", Source = "test",
            IsActive = true,
        };

    /// <summary>A diagnosis with the ancestor chain the loaded hierarchy would supply.</summary>
    private static Dictionary<string, IReadOnlyList<string>> Dx(params (string Code, string[] Ancestors)[] rows) =>
        rows.ToDictionary(r => r.Code, r => (IReadOnlyList<string>)r.Ancestors, StringComparer.Ordinal);

    [Fact]
    public void AN_NSAID_PRESCRIBED_WITH_A_CODED_PEPTIC_ULCER_IS_FLAGGED()
    {
        // The acceptance case doc 44 §5 names. The rule is written at K27; the diagnosis is coded K27.9.
        var ibuprofen = Drug("M01AE01", "ibuprofen");

        var hits = ContraindicationMatcher.Match(
            ibuprofen, Dx(("K27.9", ["K27", "K20-K31", "XI"])), isPregnant: false,
            [Rule(RuleSubjectKind.AtcClass, "M01A", "K27")]);

        hits.Should().ContainSingle();
        hits[0].Rule.Severity.Should().Be(InteractionSeverity.Contraindicated);
        hits[0].MatchedOn.Should().Contain("M01A").And.Contain("K27.9");
        // The field most likely to change the prescription.
        hits[0].Rule.ManagementEn.Should().Contain("paracetamol");
    }

    [Fact]
    public void A_rule_at_a_CATEGORY_catches_every_specific_code_underneath_it()
    {
        // Matching on the code alone would need the rule re-authored every time a coder was more precise.
        var ibuprofen = Drug("M01AE01", "ibuprofen");
        var rule = new[] { Rule(RuleSubjectKind.AtcClass, "M01A", "K27") };

        foreach (var code in new[] { "K27.0", "K27.4", "K27.9" })
        {
            ContraindicationMatcher.Match(ibuprofen, Dx((code, ["K27", "K20-K31"])), false, rule)
                .Should().ContainSingle("{0} is underneath K27", code);
        }
    }

    [Fact]
    public void An_UNRELATED_diagnosis_does_not_fire()
    {
        var ibuprofen = Drug("M01AE01", "ibuprofen");

        ContraindicationMatcher.Match(
            ibuprofen, Dx(("J06.9", ["J06", "J00-J06", "X"])), false,
            [Rule(RuleSubjectKind.AtcClass, "M01A", "K27")])
            .Should().BeEmpty();
    }

    [Fact]
    public void An_unrelated_DRUG_does_not_fire_on_the_right_diagnosis()
    {
        var amoxicillin = Drug("J01CA04", "amoxicillin");

        ContraindicationMatcher.Match(
            amoxicillin, Dx(("K27.9", ["K27"])), false,
            [Rule(RuleSubjectKind.AtcClass, "M01A", "K27")])
            .Should().BeEmpty();
    }

    // ---- pregnancy: a STATUS, not a coded diagnosis (design 44 §5) --------------------------------------

    [Fact]
    public void AN_ACE_INHIBITOR_IS_FLAGGED_FOR_A_PATIENT_RECORDED_AS_PREGNANT()
    {
        // Deliberately NOT keyed on an O00-O9A diagnosis. A rule that fired only when somebody had coded
        // pregnancy on THIS visit would catch only the patient nobody needs reminding about.
        var ramipril = Drug("C09AA05", "ramipril");

        var hits = ContraindicationMatcher.Match(
            ramipril, Dx(("I10", ["I10-I16", "IX"])), isPregnant: true,
            [Rule(RuleSubjectKind.AtcClass, "C09", DrugDiseaseContraindication.PregnancyScope)]);

        hits.Should().ContainSingle();
        hits[0].MatchedOn.Should().Contain("recorded as pregnant");
    }

    [Fact]
    public void AN_UNKNOWN_PREGNANCY_STATUS_DOES_NOT_COUNT_AS_PREGNANT()
    {
        // The judgement that decides whether this rule is usable. Firing on every patient nobody has asked
        // about would train prescribers to dismiss it within a day — and it would then mean nothing for the
        // patients it was written for. The engine reports the Unknown separately as a missing input.
        var ramipril = Drug("C09AA05", "ramipril");

        ContraindicationMatcher.Match(
            ramipril, Dx(("I10", ["I10-I16"])), isPregnant: false,
            [Rule(RuleSubjectKind.AtcClass, "C09", DrugDiseaseContraindication.PregnancyScope)])
            .Should().BeEmpty();
    }

    [Fact]
    public void A_rule_keyed_on_a_MOLECULE_fires_for_a_combination_product_containing_it()
    {
        var compound = Drug("A10BD07", "metformin", "sitagliptin");

        ContraindicationMatcher.Match(
            compound, Dx(("N18.4", ["N18", "N17-N19"])), false,
            [Rule(RuleSubjectKind.Ingredient, "metformin", "N18", InteractionSeverity.Major)])
            .Should().ContainSingle();
    }

    [Fact]
    public void With_no_hierarchy_loaded_a_category_level_rule_still_matches_its_category()
    {
        // A catalogue not yet reloaded since the closure arrived has no ancestor rows. The fallback keeps the
        // check working rather than silently reporting every patient as clear.
        var ibuprofen = Drug("M01AE01", "ibuprofen");

        ContraindicationMatcher.Match(
            ibuprofen, Dx(("K27.9", [])), false, [Rule(RuleSubjectKind.AtcClass, "M01A", "K27")])
            .Should().ContainSingle();
    }
}
