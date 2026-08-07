using FluentAssertions;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Tests;

/// <summary>
/// 28.3 — ingredient-level interaction matching (design 44 §1.2, §8).
///
/// <para>
/// <c>masterdata.drug_interaction</c> keyed a pair on two PRODUCT uuids. The Egyptian catalogue holds 22,653
/// products, so one clinical fact — warfarin interacts with NSAIDs — needed a row for every pair of BRANDS
/// containing them. The table held zero rows and would have stayed empty: not a data-entry backlog, an
/// unpopulatable model.
/// </para>
///
/// <para>
/// Everything below is a case the product-level model could not express at all.
/// </para>
/// </summary>
public class InteractionMatcherTests
{
    private static DrugComposition Drug(string? atc = null, params string[] ingredients) =>
        new(Guid.NewGuid(),
            ingredients.ToHashSet(StringComparer.Ordinal),
            atc is null ? new HashSet<string>(StringComparer.Ordinal)
                : MasterDataNormalize.AtcAncestors(atc).Append(atc).ToHashSet(StringComparer.Ordinal));

    private static InteractionRule Rule(
        RuleSubjectKind subjectKind, string subject,
        RuleSubjectKind objectKind, string obj,
        InteractionSeverity severity = InteractionSeverity.Major) =>
        new()
        {
            RuleId = Guid.NewGuid(),
            SubjectKind = subjectKind, SubjectValue = subject,
            ObjectKind = objectKind, ObjectValue = obj,
            Severity = severity,
            MechanismEn = "mechanism", MechanismAr = "آلية",
            ClinicalEffectEn = "effect", ClinicalEffectAr = "أثر",
            ManagementEn = "management", ManagementAr = "تدبير",
            Onset = InteractionOnset.Delayed, EvidenceLevel = EvidenceLevel.Established,
            Citation = "citation", Source = "test", IsActive = true,
        };

    /// <summary>warfarin × all NSAIDs — the single highest-yield rule on any interruptive list.</summary>
    private static InteractionRule WarfarinNsaids() =>
        Rule(RuleSubjectKind.Ingredient, "warfarin", RuleSubjectKind.AtcClass, "M01A");

    [Fact]
    public void ONE_CLASS_RULE_COVERS_EVERY_BRAND_OF_EVERY_NSAID()
    {
        // The whole argument for the model change. A product-level table would need a row for warfarin ×
        // ibuprofen, warfarin × diclofenac, warfarin × naproxen — and then again for every BRAND of each.
        // One rule covers all of them, and keeps covering them as products enter the market.
        var warfarin = Drug("B01AA03", "warfarin");
        var rules = new[] { WarfarinNsaids() };

        foreach (var (atc, name) in new[] { ("M01AE01", "ibuprofen"), ("M01AB05", "diclofenac"), ("M01AE02", "naproxen") })
        {
            var hits = InteractionMatcher.Match([warfarin, Drug(atc, name)], rules);
            hits.Should().HaveCount(1, "{0} is inside M01A", name);
            hits[0].Rule.Severity.Should().Be(InteractionSeverity.Major);
        }
    }

    [Fact]
    public void A_class_rule_matches_a_DESCENDANT_atc_code_not_only_an_exact_one()
    {
        // The rule is written at M01A; the product is coded M01AE01. Matching only the exact code would make
        // every class-level rule dead on arrival, because no product is coded at class level.
        var hits = InteractionMatcher.Match(
            [Drug("B01AA03", "warfarin"), Drug("M01AE01", "ibuprofen")], [WarfarinNsaids()]);

        hits.Should().ContainSingle();
        hits[0].MatchedOn.Should().Contain("warfarin").And.Contain("M01A");
    }

    [Fact]
    public void The_pair_matches_in_BOTH_directions()
    {
        // A rule is authored once, in whichever direction the pharmacist wrote it. Which medicine the
        // prescriber happens to add first must not decide whether they are warned.
        var warfarin = Drug("B01AA03", "warfarin");
        var ibuprofen = Drug("M01AE01", "ibuprofen");
        var rules = new[] { WarfarinNsaids() };

        InteractionMatcher.Match([warfarin, ibuprofen], rules).Should().ContainSingle();
        InteractionMatcher.Match([ibuprofen, warfarin], rules).Should().ContainSingle();
    }

    [Fact]
    public void A_COMBINATION_PRODUCT_IS_SCREENED_ON_EVERY_INGREDIENT_IT_CONTAINS()
    {
        // A cold-and-flu compound whose own ATC says nothing about ibuprofen. Only the decomposition finds
        // it — which is exactly the case that made the product-level model unfixable rather than merely
        // tedious to populate.
        var compound = Drug("N02BE51", "paracetamol", "ibuprofen", "pseudoephedrine");

        var hits = InteractionMatcher.Match([Drug("B01AA03", "warfarin"), compound], [WarfarinNsaids()]);

        // Ibuprofen inside the compound carries no M01A code of its own here, so the rule must match on the
        // MOLECULE. Written as an ingredient-side rule to prove that path independently of the ATC chain.
        var byMolecule = InteractionMatcher.Match(
            [Drug("B01AA03", "warfarin"), compound],
            [Rule(RuleSubjectKind.Ingredient, "warfarin", RuleSubjectKind.Ingredient, "ibuprofen")]);

        hits.Should().BeEmpty("the compound's own ATC is not inside M01A — only its molecules give it away");
        byMolecule.Should().ContainSingle();
        byMolecule[0].MatchedOn.Should().Contain("ibuprofen");
    }

    [Fact]
    public void One_rule_fires_ONCE_per_pair_however_many_ways_it_matches()
    {
        // A compound containing ibuprofen AND coded inside M01A satisfies the class side twice over. Two
        // identical warnings on one line read as two independent risks.
        var compound = Drug("M01AE01", "ibuprofen", "famotidine");

        var hits = InteractionMatcher.Match(
            [Drug("B01AA03", "warfarin"), compound],
            [WarfarinNsaids(), Rule(RuleSubjectKind.Ingredient, "warfarin", RuleSubjectKind.Ingredient, "ibuprofen")]);

        hits.Should().HaveCount(2, "two DIFFERENT rules matched — deduplication is per rule, not per pair");
        hits.Select(h => h.Rule.RuleId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_medicine_does_not_interact_with_itself()
    {
        // The same product on two lines is a DUPLICATION (Gate 5), which is a different check with a
        // different message. Reporting it here would tell a prescriber a medicine interacts with itself.
        var warfarin = Drug("B01AA03", "warfarin");

        InteractionMatcher.Match([warfarin, warfarin],
            [Rule(RuleSubjectKind.Ingredient, "warfarin", RuleSubjectKind.AtcClass, "B01AA")])
            .Should().BeEmpty();
    }

    [Fact]
    public void An_unrelated_pair_matches_nothing()
    {
        InteractionMatcher.Match(
            [Drug("A10BA02", "metformin"), Drug("R06AX13", "loratadine")], [WarfarinNsaids()])
            .Should().BeEmpty();
    }

    [Fact]
    public void A_product_with_no_ingredient_and_no_ATC_matches_nothing_and_is_flagged_unresolvable()
    {
        // 4.7% of the catalogue has no recorded ingredient and 14.8% no ATC. A product with neither cannot
        // be matched against any rule — and the caller must be told, or the prescription reads as screened.
        var unknown = Drug();

        InteractionMatcher.Match([Drug("B01AA03", "warfarin"), unknown], [WarfarinNsaids()]).Should().BeEmpty();
        unknown.IsResolvable.Should().BeFalse();
    }

    [Fact]
    public void The_triple_whammy_produces_a_finding_for_each_pair_that_carries_risk()
    {
        // NSAID + ACE inhibitor + diuretic. The acute kidney injury comes from all three together, and the
        // engine matches pairs — so the pattern is encoded as two pairwise rules and both must fire on a
        // prescription carrying all three.
        var nsaid = Drug("M01AE01", "ibuprofen");
        var acei = Drug("C09AA05", "ramipril");
        var diuretic = Drug("C03CA01", "furosemide");

        var hits = InteractionMatcher.Match([nsaid, acei, diuretic],
        [
            Rule(RuleSubjectKind.AtcClass, "M01A", RuleSubjectKind.AtcClass, "C09"),
            Rule(RuleSubjectKind.AtcClass, "M01A", RuleSubjectKind.AtcClass, "C03", InteractionSeverity.Moderate),
        ]);

        hits.Should().HaveCount(2);
        hits.Select(h => h.Rule.Severity).Should()
            .Contain(InteractionSeverity.Major).And.Contain(InteractionSeverity.Moderate);
    }

    [Fact]
    public void An_inactive_rule_is_never_matched()
    {
        // Callers pass only active rules, but the guarantee is worth pinning: an unreviewed rule has no named
        // pharmacist behind it and is not permitted to warn a prescriber.
        var inactive = WarfarinNsaids();
        inactive.IsActive = false;

        var active = new[] { inactive }.Where(r => r.IsActive).ToList();
        InteractionMatcher.Match([Drug("B01AA03", "warfarin"), Drug("M01AE01", "ibuprofen")], active)
            .Should().BeEmpty();
    }
}
