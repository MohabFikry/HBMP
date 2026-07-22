using FluentAssertions;
using Mersal.Auth;

namespace Mersal.Auth.Tests;

public class MfaEvaluatorTests
{
    [Theory]
    [InlineData("mfa")]
    [InlineData("otp")]
    [InlineData("hwk")]
    [InlineData("webauthn")]
    public void Amr_with_a_second_factor_signal_is_mfa(string amr)
        => MfaEvaluator.IsSatisfied(acr: null, amr: [amr]).Should().BeTrue();

    [Fact]
    public void Two_distinct_amr_methods_imply_mfa()
        => MfaEvaluator.IsSatisfied(acr: null, amr: ["pwd", "sms"]).Should().BeTrue();

    [Theory]
    [InlineData("aal2")]
    [InlineData("loa3")]
    [InlineData("2fa")]
    public void Acr_step_up_value_is_mfa(string acr)
        => MfaEvaluator.IsSatisfied(acr, amr: []).Should().BeTrue();

    [Fact]
    public void Single_password_factor_is_not_mfa()
        => MfaEvaluator.IsSatisfied(acr: "loa1", amr: ["pwd"]).Should().BeFalse();

    [Fact]
    public void No_acr_no_amr_is_not_mfa()
        => MfaEvaluator.IsSatisfied(acr: null, amr: []).Should().BeFalse();
}
