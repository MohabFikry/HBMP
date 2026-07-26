using FluentAssertions;
using Mersal.Migration.Core;

namespace Mersal.Migration.Tests;

public sealed class IdentifierNormalizerTests
{
    [Fact]
    public void NationalId_valid_14_digit_normalizes_and_validates()
    {
        var r = IdentifierNormalizer.Normalize("2-9001-01-01-2345-6", IdentifierKind.NationalId);
        r.Kind.Should().Be(IdentifierKind.NationalId);
        r.Value.Should().Be("29001010123456");
        r.IsValid.Should().BeTrue();
        r.Key.Should().Be("NationalId:29001010123456");
    }

    [Fact]
    public void NationalId_folds_arabic_indic_digits()
    {
        // ٢ + 9001 01 01 2345 6 → all ASCII, still 14 digits.
        var r = IdentifierNormalizer.Normalize("٢9001010123456", IdentifierKind.NationalId);
        r.Value.Should().Be("29001010123456");
        r.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("29001010123", "must be 14 digits")]          // too short
    [InlineData("19001010123456", "invalid century digit")]   // leading 1
    [InlineData("29013010123456", "invalid birth date encoded")] // month 13
    public void NationalId_invalid_reports_reason(string raw, string reasonFragment)
    {
        var r = IdentifierNormalizer.Normalize(raw, IdentifierKind.NationalId);
        r.IsValid.Should().BeFalse();
        r.Reason.Should().Contain(reasonFragment);
    }

    [Fact]
    public void Unhcr_single_C_marker_validates()
    {
        var r = IdentifierNormalizer.Normalize("776-01C01234", IdentifierKind.Unhcr);
        r.Value.Should().Be("77601C01234");
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Unhcr_without_C_marker_is_invalid()
    {
        var r = IdentifierNormalizer.Normalize("7760101234", IdentifierKind.Unhcr);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Passport_alphanumeric_validates_and_detects_when_unhinted()
    {
        var r = IdentifierNormalizer.Normalize("a1234567");
        r.Kind.Should().Be(IdentifierKind.Passport);
        r.Value.Should().Be("A1234567");
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_is_invalid()
        => IdentifierNormalizer.Normalize("   ").IsValid.Should().BeFalse();

    [Fact]
    public void Detects_national_id_from_shape_when_unhinted()
        => IdentifierNormalizer.Normalize("29001010123456").Kind.Should().Be(IdentifierKind.NationalId);
}
