using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>Per-type vital range validation (US-031 "invalid value → save blocked").</summary>
public class VitalRangeTests
{
    [Theory]
    [InlineData(VitalType.HR, 72)]
    [InlineData(VitalType.Temp, 37)]
    [InlineData(VitalType.SpO2, 98)]
    [InlineData(VitalType.BP, 120)]
    [InlineData(VitalType.Weight, 70)]
    [InlineData(VitalType.Height, 175)]
    [InlineData(VitalType.BMI, 23)]
    public void Plausible_values_pass(VitalType type, double value) =>
        VitalRange.Validate(type, (decimal)value).Should().BeNull();

    [Theory]
    [InlineData(VitalType.HR, 500)]      // impossible pulse
    [InlineData(VitalType.Temp, 60)]     // impossible temperature
    [InlineData(VitalType.SpO2, 150)]    // > 100%
    [InlineData(VitalType.Weight, 0)]    // zero weight
    public void Out_of_range_values_are_rejected(VitalType type, double value) =>
        VitalRange.Validate(type, (decimal)value).Should().NotBeNull();

    [Fact]
    public void Missing_value_is_rejected() =>
        VitalRange.Validate(VitalType.HR, null).Should().NotBeNull();

    [Fact]
    public void Canonical_unit_is_known_for_every_type()
    {
        foreach (var t in Enum.GetValues<VitalType>())
            VitalRange.CanonicalUnit(t).Should().NotBeNullOrEmpty();
    }
}
