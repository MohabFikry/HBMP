using FluentAssertions;
using Mersal.Admin.Domain;

namespace Mersal.Admin.Tests;

/// <summary>Pure governance-logic tests: the template linter blocks PHI in an outbound (SMS/email) body and requires
/// AR/EN parity (in-app is exempt from the PHI rule but still needs parity); config values are typed-validated;
/// effective-dating decides which version is in force at a date.</summary>
public class GovernanceUnitTests
{
    [Fact]
    public void Linter_rejects_a_diagnosis_field_bound_to_an_sms_body()
    {
        var r = TemplateLinter.Lint("sms", "", "", "Your visit for {diagnosis} is ready", "زيارتك لـ {diagnosis} جاهزة");
        r.Ok.Should().BeFalse();
        r.Errors.Should().Contain(e => e.StartsWith("phi-token-in-outbound") && e.Contains("diagnosis"));
    }

    [Fact]
    public void Linter_rejects_english_only_template_missing_arabic_parity()
    {
        var r = TemplateLinter.Lint("email", "Subject", "", "English body {name}", "");
        r.Ok.Should().BeFalse();
        r.Errors.Should().Contain("body-ar-required");
    }

    [Fact]
    public void Linter_passes_a_phi_free_bilingual_outbound_template()
    {
        var r = TemplateLinter.Lint("sms", "", "", "Your appointment on {date} is confirmed", "تم تأكيد موعدك في {date}");
        r.Ok.Should().BeTrue();
    }

    [Fact]
    public void In_app_channel_may_carry_a_clinical_token_but_still_needs_parity()
    {
        // Inside the authenticated portal a clinical token is allowed, but AR/EN parity is still required.
        TemplateLinter.Lint("in_app", "", "", "Result {result} available", "النتيجة {result} متاحة").Ok.Should().BeTrue();
        TemplateLinter.Lint("in_app", "", "", "Result {result} available", "").Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData(ConfigValueType.Whole, "42", true)]
    [InlineData(ConfigValueType.Whole, "4.2", false)]
    [InlineData(ConfigValueType.Boolean, "true", true)]
    [InlineData(ConfigValueType.Boolean, "yes", false)]
    [InlineData(ConfigValueType.Number, "1500.50", true)]
    [InlineData(ConfigValueType.Duration, "01:00:00", true)]
    [InlineData(ConfigValueType.Duration, "soon", false)]
    public void Config_values_are_typed_validated(ConfigValueType type, string value, bool ok)
    {
        ConfigValidation.Validate(type, value).Ok.Should().Be(ok);
    }

    [Fact]
    public void Effective_dating_picks_the_version_in_force_at_a_date()
    {
        var v1 = new MasterDataVersion
        {
            EffectiveFrom = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EffectiveTo = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var v2 = new MasterDataVersion { EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), EffectiveTo = null };

        v1.InForceAt(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)).Should().BeTrue();
        v2.InForceAt(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)).Should().BeFalse();
        v2.InForceAt(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)).Should().BeTrue();
    }
}
