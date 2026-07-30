using FluentAssertions;
using Mersal.Patient.Domain;

namespace Mersal.Patient.Tests;

/// <summary>
/// QA P0-2 — the register accepted `&lt;script&gt;x&lt;/script&gt;` as a family name and `abcdefg` as a
/// phone, with 201 Created. Names are an ALLOWLIST of Unicode letters (this registry is Arabic-first and
/// multi-script; a denylist of dangerous characters is the approach that misses one); phones are + and
/// 8–15 digits, deliberately not Egyptian-only for a refugee population carrying foreign numbers.
/// </summary>
public class PersonFieldValidationTests
{
    [Theory]
    [InlineData("Amina")]
    [InlineData("أمينة يوسف")]          // Arabic, with a space
    [InlineData("Jean-Luc O'Brien Jr.")] // hyphen, apostrophe, period
    public void Real_names_in_any_script_pass(string name) =>
        PersonFieldValidation.IsValidName(name).Should().BeTrue(name);

    [Theory]
    [InlineData("<script>x</script>")]   // the QA payload, verbatim
    [InlineData("Test <b>bold</b>")]
    [InlineData("x; DROP TABLE--")]
    [InlineData("")]
    [InlineData("   ")]
    public void Markup_and_junk_are_refused_as_names(string name) =>
        PersonFieldValidation.IsValidName(name).Should().BeFalse(name);

    [Theory]
    [InlineData("+201234567890")]
    [InlineData("01234567890")]
    [InlineData("+249 91 234 5678")]     // separators tolerated (Sudanese number)
    public void Reachable_phone_shapes_pass(string phone) =>
        PersonFieldValidation.IsValidPhone(phone).Should().BeTrue(phone);

    [Theory]
    [InlineData("abcdefg")]              // the QA payload, verbatim
    [InlineData("12345")]                // too short to be dialable
    [InlineData("+.-()")]
    public void Undialable_values_are_refused_as_phones(string phone) =>
        PersonFieldValidation.IsValidPhone(phone).Should().BeFalse(phone);

    [Fact]
    public async Task The_registrar_refuses_the_QA_payload_end_to_end()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);
        var result = await reg.RegisterAsync(
            new RegisterBeneficiaryRequest(
                "Test", "<script>x</script>", new DateOnly(1990, 1, 1), null, null,
                [new NewIdentifier(IdentifierType.NationalID, "29005121234567", null, true)],
                [new NewContact(ContactType.Phone, "abcdefg", null, true)]),
            "officer-1");

        var invalid = result.Should().BeOfType<RegistrationResult.Invalid>().Subject;
        invalid.Errors.Should().Contain(e => e.Contains("familyName"));
        invalid.Errors.Should().Contain(e => e.Contains("abcdefg"));
    }

    private sealed class FakeLookup : IIdentifierLookup
    {
        public Task<Guid?> FindActiveOwnerAsync(IdentifierType type, string norm, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(null);

        public Task<Guid?> FindActiveCardHolderAsync(string card, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(null);
    }
}
