using FluentAssertions;
using Mersal.CallCentre.Domain;

namespace Mersal.CallCentre.Tests;

/// <summary>Pure verification-policy rules (no I/O): the ≥2-identifier-type threshold, distinct/known-type
/// normalisation (the defence against a caller's VALUE being smuggled in as a "type"), and the call-ref format.</summary>
public class VerificationTests
{
    [Fact]
    public void Two_distinct_known_types_meet_the_threshold()
    {
        var types = VerificationPolicy.Normalise(["MemberNo", "DateOfBirth"]);
        VerificationPolicy.MeetsThreshold(types).Should().BeTrue();
    }

    [Fact]
    public void One_type_is_below_the_threshold()
    {
        var types = VerificationPolicy.Normalise(["MemberNo"]);
        VerificationPolicy.MeetsThreshold(types).Should().BeFalse();
    }

    [Fact]
    public void Duplicate_types_collapse_and_do_not_inflate_the_count()
    {
        var types = VerificationPolicy.Normalise(["MemberNo", "MemberNo", "MemberNo"]);
        types.Should().ContainSingle();
        VerificationPolicy.MeetsThreshold(types).Should().BeFalse();
    }

    [Fact]
    public void Unknown_types_are_dropped_so_a_recited_value_cannot_pose_as_a_type()
    {
        // "12345" (a value the caller might recite) is not a challengeable TYPE — it must not count.
        var types = VerificationPolicy.Normalise(["MemberNo", "12345", "not-a-type"]);
        types.Should().BeEquivalentTo(["MemberNo"]);
        VerificationPolicy.MeetsThreshold(types).Should().BeFalse();
    }

    [Fact]
    public void All_challengeable_types_include_phone_the_primary_entry_point()
    {
        VerificationPolicy.ChallengeableTypes.Should().Contain("Phone");
    }

    [Fact]
    public void Call_ref_is_zero_padded_and_prefixed()
    {
        CallRef.Format(2026, 42).Should().Be("CALL-2026-000042");
    }
}
