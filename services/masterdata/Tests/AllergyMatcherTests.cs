using FluentAssertions;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Tests;

/// <summary>
/// 28.1 — the allergy matcher (design 44 §1.1–§1.3).
///
/// <para>
/// THIS FILE IS THE ONE THAT DID NOT EXIST. masterdata decided an allergy conflict with a single
/// expression — <c>codes.Any(c => atcChain.Contains(c))</c> — comparing an allergen CODE
/// (<c>ALG-PENICILLIN</c>) against a drug's ATC ancestor chain (<c>J</c>, <c>J01</c>, <c>J01C</c>). Two
/// disjoint code spaces. It was a constant false for every patient and every medicine this platform has
/// ever prescribed, and the prescribing engine rendered the constant false as "no conflict with the 3
/// recorded allergies".
/// </para>
///
/// <para>
/// It survived eight phases because the engine's own tests built the allergy screen BY HAND and the matcher
/// had no test at all. Nothing in the repository ever ran a real allergen through it. Matching failures are
/// the dangerous failures of an allergy check — not transport failures — so the matching is pure and tested
/// here without a database.
/// </para>
/// </summary>
public class AllergyMatcherTests
{
    private static readonly Guid Penicillin = Guid.NewGuid();
    private static readonly Guid Peanut = Guid.NewGuid();
    private static readonly Guid Sulfa = Guid.NewGuid();

    // ---- fixtures --------------------------------------------------------------------------------------

    private static DrugComposition Drug(string? atc = null, params string[] ingredients) =>
        new(Guid.NewGuid(),
            ingredients.ToHashSet(StringComparer.Ordinal),
            atc is null ? new HashSet<string>(StringComparer.Ordinal)
                : MasterDataNormalize.AtcAncestors(atc).Append(atc).ToHashSet(StringComparer.Ordinal));

    private static AllergenMapping PenicillinAllergy(params CrossReactivityRule[] crossReactivity) =>
        new(Penicillin, "Penicillins", IsDrugMappable: true,
            new HashSet<string>(StringComparer.Ordinal) { "amoxicillin", "ampicillin", "benzylpenicillin" },
            new HashSet<string>(StringComparer.Ordinal) { "J01C" },
            crossReactivity);

    private static readonly CrossReactivityRule SideChain = new(
        "XR-PEN-CEPH-R1", "Shared R1 side chain", CrossReactivityConfidence.Moderate,
        "Shares the aminobenzyl R1 side chain.", "يشترك في السلسلة الجانبية R1.",
        "Zagursky & Pichichero 2018",
        new HashSet<string>(StringComparer.Ordinal) { "cefalexin", "cefaclor" },
        new HashSet<string>(StringComparer.Ordinal));

    private static readonly CrossReactivityRule ClassWide = new(
        "XR-PEN-CEPH-GENERAL", "Cephalosporins generally", CrossReactivityConfidence.Low,
        "Cross-reactivity without a shared side chain is low.", "التفاعل المتبادل منخفض.",
        "Picard 2019",
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal) { "J01D" });

    // ---- the case that silently passed for eight phases -------------------------------------------------

    [Fact]
    public void A_PENICILLIN_ALLERGIC_PATIENT_PRESCRIBED_AMOXICILLIN_IS_FLAGGED()
    {
        // The case doc 44 §1.1 names, and the one the old matcher could not express. Both routes to it are
        // asserted: the molecule itself, and the ATC class for a product whose ingredient text is missing.
        var byIngredient = AllergyMatcher.Screen(Drug("J01CA04", "amoxicillin"), [PenicillinAllergy()]);
        var byAtcOnly = AllergyMatcher.Screen(Drug("J01CA04"), [PenicillinAllergy()]);

        byIngredient.Strongest.Should().NotBeNull();
        byIngredient.Strongest!.Kind.Should().Be(AllergyMatchKind.ExactIngredient);
        byIngredient.Strongest.MatchedOn.Should().Contain("amoxicillin");

        byAtcOnly.Strongest.Should().NotBeNull();
        byAtcOnly.Strongest!.Kind.Should().Be(AllergyMatchKind.AtcScope);
        byAtcOnly.Strongest.MatchedOn.Should().Contain("J01C");
    }

    [Fact]
    public void A_combination_product_is_screened_on_every_ingredient_it_contains()
    {
        // Co-amoxiclav. Its ATC (J01CR02) is the COMBINATION's, and a check that looked only at the compound
        // would have to decide whether "penicillin combinations" counts. Decomposition removes the question:
        // the product contains amoxicillin, and amoxicillin is what the allergy is about.
        var coAmoxiclav = Drug("J01CR02", "amoxicillin", "clavulanic acid");

        var screen = AllergyMatcher.Screen(coAmoxiclav, [PenicillinAllergy()]);

        screen.Strongest.Should().NotBeNull();
        screen.Strongest!.Kind.Should().Be(AllergyMatchKind.ExactIngredient);
        screen.Strongest.MatchedOn.Should().Contain("amoxicillin");
    }

    [Fact]
    public void An_unrelated_medicine_does_not_match_and_the_allergy_counts_as_screened()
    {
        var metformin = Drug("A10BA02", "metformin");

        var screen = AllergyMatcher.Screen(metformin, [PenicillinAllergy()]);

        screen.Matches.Should().BeEmpty();
        // The number that entitles the engine to say Ok. A clean screen is only clean if it happened.
        screen.ScreenedAllergenCount.Should().Be(1);
        screen.UnmappedAllergens.Should().BeEmpty();
    }

    // ---- the gaps, kept distinguishable from each other -------------------------------------------------

    [Fact]
    public void An_allergen_with_no_mapping_at_all_is_reported_unmapped_and_never_screened()
    {
        var unmapped = new AllergenMapping(
            Sulfa, "Sulfonamides", IsDrugMappable: true,
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), []);

        var screen = AllergyMatcher.Screen(Drug("J01CA04", "amoxicillin"), [unmapped]);

        screen.UnmappedAllergens.Should().Equal("Sulfonamides");
        screen.ScreenedAllergenCount.Should().Be(0);
    }

    [Fact]
    public void A_food_allergen_is_neither_screened_nor_counted_as_a_gap()
    {
        // The distinction that keeps the coverage number meaningful. A peanut allergy is not a question
        // about a medicine — reporting it as unmapped would make every patient with one look like a
        // catalogue failure, and the noise would bury the drug allergens that really are unmapped.
        var peanut = new AllergenMapping(
            Peanut, "Peanuts", IsDrugMappable: false,
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), []);

        var screen = AllergyMatcher.Screen(Drug("J01CA04", "amoxicillin"), [peanut]);

        screen.UnmappedAllergens.Should().BeEmpty();
        screen.ScreenedAllergenCount.Should().Be(0);
        screen.Matches.Should().BeEmpty();
    }

    [Fact]
    public void A_medicine_with_no_ingredient_and_no_ATC_is_reported_as_unresolvable()
    {
        // 4.7% of the catalogue has no recorded active ingredient and 14.8% no ATC code. A product with
        // neither cannot be compared with ANY allergy, and that is a gap in the DRUG's row — the prescriber
        // must not be told their patient's allergy record is at fault.
        var screen = AllergyMatcher.Screen(Drug(), [PenicillinAllergy()]);

        screen.DrugResolvable.Should().BeFalse();
        screen.ScreenedAllergenCount.Should().Be(0);
        screen.UnmappedAllergens.Should().BeEmpty("the allergen IS mapped — it is the medicine that is not");
    }

    // ---- cross-reactivity, with the evidence attached ---------------------------------------------------

    [Fact]
    public void A_shared_side_chain_cephalosporin_matches_at_moderate_confidence()
    {
        var cefalexin = Drug("J01DB01", "cefalexin");

        var hit = AllergyMatcher.Screen(cefalexin, [PenicillinAllergy(SideChain, ClassWide)]).Strongest;

        hit.Should().NotBeNull();
        hit!.Kind.Should().Be(AllergyMatchKind.CrossReactivity);
        hit.Confidence.Should().Be(CrossReactivityConfidence.Moderate);
        // The confidence must reach the prescriber in words, not only as an enum on the wire.
        hit.MatchedOn.Should().Contain("moderate confidence");
        hit.StatementEn.Should().Contain("side chain");
        hit.Citation.Should().NotBeNullOrWhiteSpace("an advisory that cannot be attributed is one a clinician is right to ignore");
    }

    [Fact]
    public void A_cephalosporin_with_a_different_side_chain_matches_only_at_low_confidence()
    {
        // Ceftriaxone: a cephalosporin (J01DD04, so inside J01D) that shares no side chain with the
        // aminopenicillins. The often-quoted 10% cross-reactivity figure is not supported by current
        // evidence, and blanket cephalosporin avoidance after a penicillin label causes real harm through
        // inferior antibiotic choice — so this must report LOW, not the same alert as the side-chain case.
        var ceftriaxone = Drug("J01DD04", "ceftriaxone");

        var hit = AllergyMatcher.Screen(ceftriaxone, [PenicillinAllergy(SideChain, ClassWide)]).Strongest;

        hit.Should().NotBeNull();
        hit!.Confidence.Should().Be(CrossReactivityConfidence.Low);
        hit.MatchedOn.Should().Contain("low confidence");
    }

    [Fact]
    public void The_strongest_relationship_is_reported_when_several_hold()
    {
        // Cefalexin is in J01D (low, class-wide) AND shares the R1 side chain (moderate). Reporting the
        // weaker of the two would understate a real risk; reporting both would make the weaker one look
        // like an additional, independent finding.
        var cefalexin = Drug("J01DB01", "cefalexin");

        var hit = AllergyMatcher.Screen(cefalexin, [PenicillinAllergy(ClassWide, SideChain)]).Strongest;

        hit!.Confidence.Should().Be(CrossReactivityConfidence.Moderate);
    }

    [Fact]
    public void A_direct_match_outranks_a_cross_reaction()
    {
        // "Contains amoxicillin, which your penicillin allergy covers" is a stronger and more useful
        // statement than "may cross-react", and the prescriber should be shown the most direct true one.
        var amoxicillin = Drug("J01CA04", "amoxicillin");

        var hit = AllergyMatcher.Screen(amoxicillin, [PenicillinAllergy(SideChain, ClassWide)]).Strongest;

        hit!.Kind.Should().Be(AllergyMatchKind.ExactIngredient);
    }
}
