using FluentAssertions;
using Mersal.MasterData.Domain;

namespace Mersal.MasterData.Tests;

/// <summary>
/// The catalogue's own presentation of a drug's name.
///
/// <para><b>Why this is a data rule and not a CSS rule.</b> The trade name arrives ALL CAPS from one source
/// and all-lowercase from the other, so the same list renders "PARTEN MASSAGE SPRAY" directly above
/// "gastrodomina 40mg 10 tab". Casing it at the display layer would fix the one screen that remembered to and
/// leave the search index, the exports, and — the one that matters — the name SNAPSHOTTED onto a prescription
/// line and printed on the patient's copy, all disagreeing with each other.</para>
///
/// <para><b>It cannot orphan a row.</b> The natural key is <see cref="MasterDataNormalize.DrugCode"/>, which
/// upper-cases and strips punctuation, so a re-cased name still adopts the same existing uuid on reload and
/// every indication, interaction and prescription line keeps pointing at it.</para>
/// </summary>
public class DisplayNameCasingTests
{
    [Theory]
    [InlineData("PARTEN MASSAGE SPRAY", "Parten Massage Spray")]
    [InlineData("gastrodomina 40mg 10 tab", "Gastrodomina 40mg 10 Tab")]
    [InlineData("ZAVEDOS 5 MG VIAL(N/A)", "Zavedos 5 MG Vial(N/A)")]
    public void A_name_is_capitalised_word_by_word(string raw, string expected)
    {
        MasterDataNormalize.DisplayName(raw).Should().Be(expected);
    }

    [Theory]
    // A unit of measure stays UPPER, from whichever source it arrived in. "100 IU" is how a prescriber
    // writes it and "100 Iu" is not; more to the point, the two sources spell it differently ("MG" from one,
    // "mg" from the other) and word-capitalising both would still leave the list saying "Mg" where the
    // strength beside it says "mg".
    [InlineData("herceptin 150 mg vial for i.v. inf", "Herceptin 150 MG Vial For I.V. Inf")]
    [InlineData("actrapid hm 100 i.u./ml 5*3ml penfills", "Actrapid Hm 100 I.U./ML 5*3ml Penfills")]
    [InlineData("SULPERAZON 1.5 GM (I.V/I.M) INJ.", "Sulperazon 1.5 GM (I.V/I.M) Inj.")]
    public void A_unit_of_measure_stays_upper_case(string raw, string expected)
    {
        MasterDataNormalize.DisplayName(raw).Should().Be(expected);
    }

    [Theory]
    // A token carrying a DIGIT is left exactly as written. "40mg" is a strength a prescriber reads as a
    // number, and "40Mg" or "40MG" is a different-looking dose of the same drug — the last thing this list
    // should introduce is a second spelling of a strength.
    [InlineData("bistol 2.5mg 20 f.c. tab", "Bistol 2.5mg 20 F.C. Tab")]
    [InlineData("aclasta 5mg/100ml soln. for inf", "Aclasta 5mg/100ml Soln. For Inf")]
    public void A_token_containing_a_digit_keeps_its_own_casing(string raw, string expected)
    {
        MasterDataNormalize.DisplayName(raw).Should().Be(expected);
    }

    [Theory]
    // Each alphabetic RUN is capitalised, so dotted abbreviations and joined ingredient lists read correctly
    // rather than getting one capital at the front of a compound.
    [InlineData("SULPERAZON 1.5 GM (I.V/I.M) INJ. (HOSPITAL PRICE)", "Sulperazon 1.5 GM (I.V/I.M) Inj. (Hospital Price)")]
    [InlineData("hydrochlorothiazide+olmesartan", "Hydrochlorothiazide+Olmesartan")]
    [InlineData("LIDOCAINE - AESCIN - METHLY SALLCYLATE", "Lidocaine - Aescin - Methly Sallcylate")]
    [InlineData("guava leaves+tilia flower+fennel oil", "Guava Leaves+Tilia Flower+Fennel Oil")]
    public void Every_alphabetic_run_gets_its_own_capital(string raw, string expected)
    {
        MasterDataNormalize.DisplayName(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void An_absent_name_stays_absent(string? raw, string? expected)
    {
        // Absence is carried through. A blank active ingredient must not become an empty-looking string that
        // renders as a value — 4.7% of the workbook has none.
        MasterDataNormalize.DisplayName(raw).Should().Be(expected);
    }

    [Fact]
    public void Casing_a_name_does_not_change_its_natural_key()
    {
        // The property that makes this safe to apply to 31,651 existing rows: the upsert matches on
        // `drug_code`, so a re-cased name adopts the row it already had rather than inserting a duplicate.
        const string raw = "gastrodomina 40mg 10 tab";

        MasterDataNormalize.DrugCode(MasterDataNormalize.DisplayName(raw)!)
            .Should().Be(MasterDataNormalize.DrugCode(raw));
    }

    [Fact]
    public void Casing_is_idempotent()
    {
        // Reloads are routine, so the second pass over an already-cased catalogue must be a no-op rather than
        // a slow drift toward some other spelling.
        var once = MasterDataNormalize.DisplayName("SULPERAZON 1.5 GM (I.V/I.M) INJ. (HOSPITAL PRICE)");

        MasterDataNormalize.DisplayName(once).Should().Be(once);
    }
}
