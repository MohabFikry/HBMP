using Mersal.Ingredients;
using FluentAssertions;
using Xunit;

namespace Mersal.ClinicalValidation.Tests;

/// <summary>
/// Turning the Egyptian catalogue's <c>scientific_name</c> into a name openFDA answers to.
/// </summary>
/// <remarks>
/// Every dangerous failure of the live label check is a naming failure rather than a transport failure, so
/// these run without a network. The near-miss is the case that matters: a lookup that returns nothing is
/// visibly "not checked", while a lookup that returns the <i>wrong molecule's</i> label returns a 200, a full
/// interactions section, and complete confidence.
/// </remarks>
public class IngredientTokenTests
{
    [Theory]
    [InlineData("Diclofenac Sodium", "diclofenac")]
    [InlineData("Rosuvastatin Calcium", "rosuvastatin")]
    [InlineData("Esomeprazole  Magnesium", "esomeprazole")]
    [InlineData("amlodipine besylate", "amlodipine")]
    public void Normalize_strips_a_trailing_salt(string input, string expected)
        => IngredientTokens.Normalize(input).Should().Be(expected);

    [Fact]
    public void Normalize_does_not_strip_a_salt_word_that_is_part_of_the_substance()
    {
        // The bug this pins: stripping "sodium" anywhere leaves "chloride", which openFDA answers with
        // BENZALKONIUM CHLORIDE — a disinfectant — and the platform would have read interaction advice for
        // the wrong substance off a confidently retrieved label. Suffix-only stripping is the fix.
        IngredientTokens.Normalize("Sodium Chloride").Should().Be("sodium chloride");
        IngredientTokens.Normalize("sodium valproate").Should().Be("sodium valproate");
    }

    [Fact]
    public void Candidates_tries_the_unmodified_name_before_the_salt_stripped_one()
    {
        // "Warfarin sodium" is itself a published label name. Trying the stripped form first would answer
        // some drugs from a label that is not theirs.
        var candidates = IngredientTokens.Candidates("Warfarin Sodium");

        candidates.Should().ContainInOrder("warfarin sodium", "warfarin");
    }

    [Theory]
    [InlineData("paracetamol", "acetaminophen")]
    [InlineData("salbutamol", "albuterol")]
    [InlineData("adrenaline", "epinephrine")]
    public void Candidates_offers_the_us_spelling_for_an_international_name(string inn, string usan)
    {
        // Egypt prescribes in INN. All three of these return nothing from openFDA under their INN spelling,
        // and they are among the most-dispensed medicines on the formulary — without the map the check would
        // report "no label published" for a large part of the catalogue and look like a coverage gap.
        IngredientTokens.Candidates(inn).Should().Contain(usan);
    }

    [Fact]
    public void Candidates_prefers_the_bracketed_name_the_catalogue_supplies()
    {
        // The catalogue writes "paracetamol(acetaminophen)" precisely where the two spellings diverge.
        IngredientTokens.Candidates("paracetamol(acetaminophen)").Should().StartWith("acetaminophen");
    }

    [Fact]
    public void Candidates_splits_a_combination_into_its_components()
    {
        // No label is published under the joined string, and a combination's interactions are its parts'.
        var candidates = IngredientTokens.Candidates("amoxicillin+clavulanic acid");

        candidates.Should().Contain("amoxicillin").And.Contain("clavulanic acid");
    }

    [Fact]
    public void Candidates_of_a_product_with_no_ingredient_is_empty()
    {
        // 2,786 catalogue products have no scientific name. Empty means the check says so, rather than
        // searching for nothing and reporting whatever comes back.
        IngredientTokens.Candidates(null).Should().BeEmpty();
        IngredientTokens.Candidates("   ").Should().BeEmpty();
    }

    [Fact]
    public void An_exact_match_accepts_the_salt_form_of_the_same_molecule()
    {
        IngredientTokens.IsExactMatch("atorvastatin", "ATORVASTATIN CALCIUM").Should().BeTrue();
        IngredientTokens.IsExactMatch("paracetamol", "ACETAMINOPHEN").Should().BeTrue();
    }

    [Fact]
    public void An_exact_match_rejects_a_combination_product()
    {
        // openFDA returns the amoxicillin/clavulanate label first for a plain "amoxicillin" search. The
        // combination's interactions section is not the plain product's, so this must not be accepted.
        IngredientTokens.IsExactMatch("amoxicillin", "AMOXICILLIN AND CLAVULANATE POTASSIUM").Should().BeFalse();
        IngredientTokens.IsExactMatch("chloride", "BENZALKONIUM CHLORIDE").Should().BeFalse();
    }
}

/// <summary>Reading a drug's name out of another drug's label interactions prose.</summary>
public class LabelInteractionScanTests
{
    private const string WarfarinText =
        "7 DRUG INTERACTIONS Drugs may interact with warfarin through pharmacodynamic mechanisms. "
        + "Concomitant use of amiodarone increases the INR and the risk of bleeding. "
        + "No interaction was observed with omeprazole in a dedicated study.";

    [Fact]
    public void Finds_a_named_drug_and_returns_the_sentence_that_named_it()
    {
        var hit = LabelInteractionScan.Find(WarfarinText, ["amiodarone"]);

        hit.Should().NotBeNull();
        hit!.Term.Should().Be("amiodarone");
        // The sentence, not a verdict. The prescriber is the one who can tell "increases the risk of
        // bleeding" from "no interaction was observed", and the label's own wording is the honest thing to
        // hand them — this code has no source for a severity grade.
        hit.Sentence.Should().Contain("increases the INR");
    }

    [Fact]
    public void Matches_a_synonym_when_the_label_uses_the_other_spelling()
    {
        var hit = LabelInteractionScan.Find(
            "Concomitant acetaminophen may potentiate the effect.", IngredientTokens.Synonyms("paracetamol"));

        hit.Should().NotBeNull();
    }

    [Fact]
    public void Matches_whole_words_only()
    {
        // "iron" inside "environment" would fire on almost every label, and a check that cries wolf trains
        // prescribers to dismiss it — which costs more than it ever catches.
        LabelInteractionScan.Find("Store in a dry environment away from light.", ["iron"]).Should().BeNull();
    }

    [Fact]
    public void Reports_nothing_when_the_label_has_no_interactions_section()
    {
        LabelInteractionScan.Find(null, ["amiodarone"]).Should().BeNull();
        LabelInteractionScan.Find("   ", ["amiodarone"]).Should().BeNull();
    }

    [Fact]
    public void Returns_a_mention_even_when_the_label_says_there_is_no_interaction()
    {
        // Deliberate. This code cannot reliably parse negation out of regulatory prose, so it surfaces the
        // sentence and lets the prescriber read it. Suppressing "no interaction was observed" would mean
        // guessing at meaning, and guessing wrong in the other direction hides a real warning.
        var hit = LabelInteractionScan.Find(WarfarinText, ["omeprazole"]);

        hit.Should().NotBeNull();
        hit!.Sentence.Should().Contain("No interaction was observed");
    }
}

/// <summary>
/// 28.1 — the canonical <c>ingredient_key</c> a product resolves to.
///
/// <para>
/// This is the join between the catalogue and every curated clinical rule, and it fails SILENTLY when it
/// fails: a penicillin-allergy mapping written against "amoxicillin" simply matches nothing, an interaction
/// rule and the product it should fire on become two different molecules, and the duplicate-therapy check
/// compares two spellings of one drug and reports no duplication. Nothing errors; the checks just stop
/// finding things.
/// </para>
///
/// <para>
/// Every case below was taken from what the Egyptian catalogue actually contains, not from what it ought to.
/// </para>
/// </summary>
public class IngredientCanonicalTests
{
    [Theory]
    // The one that mattered most: 220 products spell it the British way and NONE spell it the INN way.
    [InlineData("amoxycillin", "amoxicillin")]
    [InlineData("Amoxycillin Trihydrate", "amoxicillin")]
    // Sulfonamides, where the catalogue is split 21-to-1 in favour of the British spelling.
    [InlineData("sulphamethoxazole", "sulfamethoxazole")]
    [InlineData("sulphasalazine", "sulfasalazine")]
    [InlineData("sulphadiazine", "sulfadiazine")]
    // Every other cephalosporin in the catalogue is already 'cef'; this one lagged.
    [InlineData("cephradine", "cefradine")]
    // INN/USAN, which was already handled and must stay handled.
    [InlineData("acetaminophen", "paracetamol")]
    [InlineData("albuterol", "salbutamol")]
    // Salt forms resolve to the molecule.
    [InlineData("Diclofenac Sodium", "diclofenac")]
    [InlineData("warfarin sodium", "warfarin")]
    public void A_molecule_has_ONE_key_however_the_catalogue_spells_it(string written, string expected)
    {
        IngredientTokens.Canonical(written).Should().Be(expected);
    }

    [Theory]
    // The reason this is a targeted fold and not a general ph→f normaliser: these would become molecules
    // that do not exist, and a rule written against the real name would then never match the product.
    [InlineData("morphine")]
    [InlineData("phenytoin")]
    [InlineData("amphotericin b")]
    [InlineData("phenylephrine")]
    public void The_fold_does_not_rewrite_names_that_merely_contain_ph(string name)
    {
        // Names carrying "ph" for reasons that are not the sulfonamide orthography. A general ph→f
        // normaliser would turn these into molecules that do not exist, and a rule written against the real
        // name would then never match the product — the same silent divergence, introduced by the fix.
        //
        // NOTE: names inside the INN/USAN map are excluded from this test on purpose. "phenobarbital" maps
        // to "phenobarbitone" through that map, which is a different mechanism and is consistent in both
        // directions, so it produces one key rather than two.
        IngredientTokens.Canonical(name).Should().Be(name);
    }

    [Fact]
    public void A_COMBINATION_RESOLVES_TO_ONE_KEY_PER_MOLECULE()
    {
        // Co-amoxiclav as the catalogue writes it. Both molecules, canonical spelling, no synonym noise —
        // this is what makes the product screen as amoxicillin for a penicillin allergy.
        IngredientTokens.Components("amoxycillin + clavulanic acid")
            .Should().Equal("amoxicillin", "clavulanic acid");
    }

    [Fact]
    public void A_bracketed_alternative_yields_the_name_the_prescriber_uses()
    {
        // Candidates() prefers the bracketed USAN because it is searching an FDA label. Choosing a catalogue
        // KEY is the opposite question, and returning both would put one molecule in the catalogue twice.
        IngredientTokens.Components("paracetamol(acetaminophen)").Should().Equal("paracetamol");
    }

    [Fact]
    public void A_product_with_no_usable_ingredient_resolves_to_NOTHING()
    {
        // 1,093 real products are in this state. Absence is load-bearing: the ingredient-level checks report
        // a medicine they could not resolve rather than passing it.
        IngredientTokens.Components(null).Should().BeEmpty();
        IngredientTokens.Components("   ").Should().BeEmpty();
        IngredientTokens.Components("12").Should().BeEmpty();
    }
}
