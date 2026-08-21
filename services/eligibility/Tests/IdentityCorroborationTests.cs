using FluentAssertions;
using Mersal.Eligibility.Domain;
using Mersal.Eligibility.Infrastructure;

namespace Mersal.Eligibility.Tests;

/// <summary>
/// 33.9 — the rule that decides whether the name agrees with the card.
/// </summary>
/// <remarks>
/// <para>The eligibility screen used to search on one free-text box and check the FIRST hit, so "Ahmed" was
/// enough to open the coverage of whichever Ahmed the database returned first. The plan, the remaining cap
/// and the visit verdict on screen belonged to a person nobody had picked, and nothing on the card said a
/// choice had been made.</para>
///
/// <para>This is the whole of what replaced it, so it is tested here as a function rather than through HTTP:
/// every case worth arguing about — a fragment too short to narrow anything, a match on the family name
/// only, a hyphenated name, a term the record does not carry — is a question about this method and nothing
/// else.</para>
/// </remarks>
public class IdentityCorroborationTests
{
    [Theory]
    [InlineData("Amal")]        // the given name, in full
    [InlineData("amal")]        // case is not part of the rule
    [InlineData("Hassan")]      // the FAMILY name alone is enough — a desk is as likely to be given either
    [InlineData("Am")]          // the floor: two characters
    [InlineData("Amal Hassan")] // both, which is how most people say it
    [InlineData("  Hass  ")]    // surrounding space is the operator's, not the data's
    public void A_name_the_record_carries_corroborates_it(string offered) =>
        IdentityCorroboration.NameCorroborates("Amal", "Hassan", offered).Should().BeTrue();

    [Theory]
    [InlineData("Yusuf")]       // a different person entirely
    [InlineData("Amal Khalil")] // right given name, wrong family — the near miss that matters most
    [InlineData("Hassanein")]   // longer than the recorded token, so not a prefix of it
    public void A_name_the_record_does_not_carry_refuses(string offered) =>
        IdentityCorroboration.NameCorroborates("Amal", "Hassan", offered).Should().BeFalse();

    /// <summary>
    /// A single letter is refused, and this is the assertion that keeps the fix a fix.
    /// </summary>
    /// <remarks>
    /// One character prefix-matches a large fraction of any name list, so accepting it would restore the old
    /// behaviour at the cost of one keystroke — the operator types the card number, adds "A", and is back to
    /// opening whoever comes first.
    /// </remarks>
    [Theory]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Amal H")]  // every TERM must clear the floor, not just the first
    public void A_fragment_too_short_to_narrow_anything_is_refused(string? offered)
    {
        IdentityCorroboration.IsUsableFragment(offered).Should().BeFalse();
        IdentityCorroboration.NameCorroborates("Amal", "Hassan", offered).Should().BeFalse();
    }

    /// <summary>
    /// Typing MORE must narrow, never widen.
    /// </summary>
    /// <remarks>
    /// If any-term-matches were the rule, adding a family name to a correct given name would make a wrong
    /// record easier to open rather than harder — the operator's extra care would work against them, which
    /// is the worst property a check like this can have.
    /// </remarks>
    [Fact]
    public void Adding_a_term_can_only_narrow_the_match()
    {
        IdentityCorroboration.NameCorroborates("Amal", "Hassan", "Amal").Should().BeTrue();
        IdentityCorroboration.NameCorroborates("Amal", "Hassan", "Amal Farouk").Should().BeFalse();
    }

    /// <summary>
    /// "Al-Sayed" is one word with two parts, and a desk given "Sayed" is right to expect it to match.
    /// </summary>
    /// <remarks>
    /// Splitting only on whitespace would refuse a correct name and send the operator hunting for a fault
    /// that is not there — which is how a check stops being used.
    /// </remarks>
    [Theory]
    [InlineData("Sayed")]
    [InlineData("Al")]
    [InlineData("al-sayed")]
    public void A_hyphenated_name_matches_on_either_part(string offered) =>
        IdentityCorroboration.NameCorroborates("Omar", "Al-Sayed", offered).Should().BeTrue();

    [Fact]
    public void An_arabic_name_is_matched_the_same_way()
    {
        // The rule is about tokens and prefixes, not about a script. Pinned because a future "normalise the
        // name" change is exactly where Arabic quietly stops matching itself.
        IdentityCorroboration.NameCorroborates("أمل", "حسن", "حسن").Should().BeTrue();
        IdentityCorroboration.NameCorroborates("أمل", "حسن", "يوسف").Should().BeFalse();
    }

    [Fact]
    public void A_record_with_no_name_corroborates_nothing()
    {
        // Default-DENY. An empty name column must not become a record anything matches — that would make the
        // least complete rows the easiest ones to open.
        IdentityCorroboration.NameCorroborates("", "", "Amal").Should().BeFalse();
        IdentityCorroboration.NameCorroborates(null, null, "Amal").Should().BeFalse();
    }
}

/// <summary>
/// 33.9 — resolving ONE member from something a beneficiary can present.
/// </summary>
/// <remarks>
/// The other half of the fix. <c>SearchAsync</c> matches names with ILIKE and returns a list; this returns
/// the member or nothing, and there is deliberately no first-of-several to fall back on.
/// </remarks>
public class PresentedIdentifierTests
{
    private static async Task<InMemoryReceptionIndex> Seed()
    {
        var idx = new InMemoryReceptionIndex();
        await idx.UpsertAsync(new ReceptionDocument
        {
            BeneficiaryId = Guid.NewGuid(), MemberNo = "MRS-M-2026-000001",
            // 33.9b — the number on the CARD, which is not the member number.
            CardNumber = "MRS-CARD-4821",
            GivenName = "Layla", FamilyName = "Haddad", Status = "Active",
            NationalId = "29001011234567", Passport = "A1234567", RefugeeId = "REF-99",
            UnhcrNo = "UNHCR-42", PolicyNo = "POL-1", PrimaryPhone = "+201000000001",
        });
        return idx;
    }

    [Theory]
    [InlineData("MRS-M-2026-000001")]  // the member number — the enrolment key
    [InlineData("MRS-CARD-4821")]      // the CARD number — a different identifier, and the one they hand over
    [InlineData("29001011234567")]     // national ID
    [InlineData("A1234567")]           // passport
    [InlineData("REF-99")]             // refugee ID
    [InlineData("UNHCR-42")]           // UNHCR number
    [InlineData("POL-1")]              // policy, where it names exactly one person
    public async Task Each_thing_a_beneficiary_can_present_resolves_the_member(string presented)
    {
        var doc = await (await Seed()).FindByPresentedIdentifierAsync(presented);
        doc.Should().NotBeNull();
        doc!.MemberNo.Should().Be("MRS-M-2026-000001");
    }

    /// <summary>
    /// A phone number is NOT a way in, and neither is a partial card.
    /// </summary>
    /// <remarks>
    /// <para>A household shares one number, so a phone identifies a family and not a person — which is the
    /// one thing this method exists to do. <c>SearchAsync</c> still matches it, correctly: the call centre
    /// takes a call from whoever is holding the phone and searching is its job.</para>
    ///
    /// <para>The partial is the other half. Equality, never ILIKE: a card number typed short must find
    /// nobody rather than the first member whose number begins that way.</para>
    /// </remarks>
    [Theory]
    [InlineData("+201000000001")]
    [InlineData("MRS-M-2026")]
    [InlineData("2900101")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Something_that_is_not_an_individual_identifier_resolves_nobody(string presented) =>
        (await (await Seed()).FindByPresentedIdentifierAsync(presented)).Should().BeNull();

    /// <summary>
    /// The card number and the member number are both accepted, and they are not the same string.
    /// </summary>
    /// <remarks>
    /// <c>member_no</c> is the enrolment key policy-service issues; <c>card_number</c> is what
    /// patient-service normalizes and prints on the object a beneficiary carries. The lookup matched every
    /// identifier except the second, so a desk typing what was in front of them found nobody and fell back
    /// to searching by name — which is the thing the verified lookup exists to stop.
    /// </remarks>
    [Fact]
    public async Task The_card_number_and_the_member_number_both_resolve_and_are_not_the_same_field()
    {
        var idx = await Seed();
        var byCard = await idx.FindByPresentedIdentifierAsync("MRS-CARD-4821");
        var byMember = await idx.FindByPresentedIdentifierAsync("MRS-M-2026-000001");

        byCard!.BeneficiaryId.Should().Be(byMember!.BeneficiaryId, "they name one person");
        byCard.CardNumber.Should().NotBe(byCard.MemberNo, "and they are two different identifiers");
    }

    [Fact]
    public async Task A_policy_covering_more_than_one_person_resolves_nobody()
    {
        // A family policy names a household. Returning the first member of it would be the original defect
        // wearing a different identifier, so the ambiguous case is refused rather than resolved.
        var idx = await Seed();
        await idx.UpsertAsync(new ReceptionDocument
        {
            BeneficiaryId = Guid.NewGuid(), MemberNo = "MRS-M-2026-000002",
            GivenName = "Karim", FamilyName = "Haddad", Status = "Active", PolicyNo = "POL-1",
        });

        (await idx.FindByPresentedIdentifierAsync("POL-1")).Should().BeNull();
        // The individual cards still work — only the shared identifier is refused.
        (await idx.FindByPresentedIdentifierAsync("MRS-M-2026-000002")).Should().NotBeNull();
    }
}
