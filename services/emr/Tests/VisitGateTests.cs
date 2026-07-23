using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

public class VisitGateTests
{
    [Fact]
    public void Active_member_is_allowed_with_no_guidance()
    {
        var r = VisitGate.Evaluate(MemberStatus.Active);
        r.Allowed.Should().BeTrue();
        r.Guidance.Should().BeNull();
    }

    [Theory]
    [InlineData(MemberStatus.Pending)]
    [InlineData(MemberStatus.Suspended)]
    [InlineData(MemberStatus.Expired)]
    [InlineData(MemberStatus.Blocked)]
    [InlineData(MemberStatus.Inactive)]
    public void Non_active_member_is_blocked_with_actionable_guidance(MemberStatus status)
    {
        var r = VisitGate.Evaluate(status);
        r.Allowed.Should().BeFalse();
        r.Guidance.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Blocked_status_guidance_mentions_director_override()
        => VisitGate.Evaluate(MemberStatus.Blocked).Guidance.Should().Contain("director");

    [Fact]
    public void Expired_status_guidance_routes_to_case_manager()
        => VisitGate.Evaluate(MemberStatus.Expired).Guidance.Should().Contain("Case Manager");

    [Fact]
    public void Only_active_is_allowed_across_all_statuses()
    {
        foreach (var s in Enum.GetValues<MemberStatus>())
            VisitGate.Evaluate(s).Allowed.Should().Be(s == MemberStatus.Active);
    }
}

public class EncounterNoTests
{
    [Fact]
    public void Formats_as_ENC_year_sequence()
        => EncounterNo.Format(2026, 42).Should().Be("ENC-2026-000042");

    [Fact]
    public void Sequence_is_zero_padded_to_six_digits()
        => EncounterNo.Format(2026, 1).Should().Be("ENC-2026-000001");
}
