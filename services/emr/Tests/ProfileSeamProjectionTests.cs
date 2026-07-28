using FluentAssertions;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Emr.Tests;

/// <summary>
/// Phase 20.2 — the profile-context seam must decide <b>per section</b>, not per endpoint.
///
/// <para>The defect this pins: <see cref="ProfileSeam.Check"/> passes when ANY requested section is visible,
/// which is the right answer to "may you make this call" and the wrong answer to "what may you receive".
/// Reception has an <c>encounters</c> cell and NO <c>pastMedicalHistory</c> cell, so a single gate handed it
/// the active ICD diagnosis list and the clinician's narrative.</para>
///
/// <para>profile-service would have dropped that section afterwards and the screen would have looked correct —
/// which is exactly why it had to be fixed in emr. Design 39 §1 is two INDEPENDENT layers: a layer that
/// over-serves and relies on the next one to trim is not a layer, and every other caller of this seam gets the
/// untrimmed answer.</para>
/// </summary>
public class ProfileSeamProjectionTests
{
    private static ProfileContext Ctx(string role, bool treating = false) => new()
    {
        Roles = new HashSet<string>(StringComparer.Ordinal) { role },
        TreatingRelationship = treating,
    };

    private static bool MayReadHistory(ProfileContext c) =>
        ProfilePolicies.Decide(ProfileSections.PastMedicalHistory, c) is { State: ProfileSectionState.Visible };

    private static bool MayReadEncounters(ProfileContext c) =>
        ProfilePolicies.Decide(ProfileSections.Encounters, c) is { State: ProfileSectionState.Visible };

    [Theory]
    [InlineData("reception")]
    [InlineData("finance")]
    [InlineData("claims_officer")]
    [InlineData("beneficiary_mgmt")]
    public void An_operational_role_reaches_the_seam_for_encounters_and_gets_NO_diagnoses(string role)
    {
        var ctx = Ctx(role);

        // It may make the call — the endpoint is not 403 for these roles.
        ProfileSeam.Check(Principal(role), ctx,
            ProfileSections.PastMedicalHistory, ProfileSections.Encounters).Should().BeNull();

        // …and it receives visit LOGISTICS only. The condition list and the narrative are not read at all.
        MayReadEncounters(ctx).Should().BeTrue("'{0}' has an encounters cell", role);
        MayReadHistory(ctx).Should().BeFalse(
            "'{0}' has NO past-medical-history cell — the seam must not return conditions or the narrative", role);
    }

    [Fact]
    public void A_treating_clinician_receives_both()
    {
        var ctx = Ctx("doctor", treating: true);
        MayReadHistory(ctx).Should().BeTrue();
        MayReadEncounters(ctx).Should().BeTrue();
    }

    [Fact]
    public void A_non_treating_doctor_reaches_neither_and_is_refused_the_call()
    {
        // Both cells degrade to Restricted, so there is nothing to fetch and the seam says so rather than
        // returning an empty body that would read as "this patient has no history".
        var ctx = Ctx("doctor");
        MayReadHistory(ctx).Should().BeFalse();
        MayReadEncounters(ctx).Should().BeFalse();
        ProfileSeam.Check(Principal("doctor"), ctx,
            ProfileSections.PastMedicalHistory, ProfileSections.Encounters).Should().NotBeNull();
    }

    [Fact]
    public void A_lab_is_refused_the_seam_outright()
    {
        // A lab has neither cell: it gets identity, allergies and its own orders, and nothing here.
        var ctx = Ctx("lab_tech");
        ProfileSeam.Check(Principal("lab_tech"), ctx,
            ProfileSections.PastMedicalHistory, ProfileSections.Encounters).Should().NotBeNull();
    }

    [Fact]
    public void The_medical_approval_team_receives_both_without_a_treating_relationship()
    {
        // Standing oversight is the whole point of that role; the SENSITIVE-result gate is a separate rule
        // and lives in orders, not here.
        var ctx = Ctx("medical_approval");
        MayReadHistory(ctx).Should().BeTrue();
        MayReadEncounters(ctx).Should().BeTrue();
    }

    private static HbmpPrincipal Principal(string role) => new()
    {
        Subject = "u-1",
        Roles = new HashSet<string>(StringComparer.Ordinal) { role },
        Scopes = new HashSet<string>(StringComparer.Ordinal) { "profile:read" },
        TenantId = "t0",
        MfaSatisfied = true,
    };
}
