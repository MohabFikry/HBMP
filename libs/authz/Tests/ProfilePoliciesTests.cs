using FluentAssertions;

namespace Mersal.Authz.Tests;

/// <summary>
/// Phase 20 — the role × section matrix of design 39 §4, asserted cell by cell.
///
/// <para>This is the unit-level half of the guarantee. It proves the DECISION is right for every role; the
/// reflection tests in profile-service prove the SERIALIZED PAYLOAD then matches the decision. Both are needed:
/// a correct matrix serialized carelessly still leaks, and a careful serializer over a wrong matrix leaks
/// exactly as much.</para>
/// </summary>
public class ProfilePoliciesTests
{
    private static ProfileContext Ctx(string role, bool treating = false, bool assigned = false,
        bool grant = false, string? providerId = null) => new()
        {
            Roles = new HashSet<string>(StringComparer.Ordinal) { role },
            TreatingRelationship = treating, CaseAssignment = assigned,
            SensitiveGrantActive = grant, ProviderId = providerId,
        };

    private static ProfileSectionState? StateOf(string role, string section, bool treating = false,
        bool assigned = false) => ProfilePolicies.Decide(section, Ctx(role, treating, assigned))?.State;

    // ---------------------------------------------------------------- the hard separations

    [Theory]
    [InlineData(ProfileSections.PastMedicalHistory)]
    [InlineData(ProfileSections.Investigations)]
    [InlineData(ProfileSections.Prescriptions)]
    [InlineData(ProfileSections.Financial)]
    public void Reception_never_receives_a_clinical_or_financial_section(string section)
    {
        // reception ≠ EMR. Not restricted — ABSENT: there is no cell, so the key never appears in the response
        // and the owning service is never even called.
        ProfilePolicies.Decide(section, Ctx("reception")).Should().BeNull();
    }

    [Theory]
    [InlineData(ProfileSections.PastMedicalHistory)]
    [InlineData(ProfileSections.Investigations)]
    [InlineData(ProfileSections.Prescriptions)]
    public void The_call_centre_never_receives_a_clinical_section(string section) =>
        ProfilePolicies.Decide(section, Ctx("call_center")).Should().BeNull();

    [Theory]
    [InlineData(ProfileSections.PastMedicalHistory)]
    [InlineData(ProfileSections.Investigations)]
    [InlineData(ProfileSections.Prescriptions)]
    [InlineData(ProfileSections.Alerts)]
    public void Finance_never_receives_a_clinical_section(string section)
    {
        // Including alerts: an allergy is a clinical fact, and "finance sees no diagnosis" is not satisfied by
        // withholding the diagnosis while handing over the allergy list.
        ProfilePolicies.Decide(section, Ctx("finance")).Should().BeNull();
        ProfilePolicies.Decide(section, Ctx("claims_officer")).Should().BeNull();
    }

    [Fact]
    public void Finance_and_claims_never_receive_the_photo() =>
        ProfilePhotoAccess.MayView(["finance"]).Should().BeFalse();

    [Theory]
    [InlineData("lab_tech")]
    [InlineData("imaging_tech")]
    [InlineData("pharmacist")]
    [InlineData("org_admin")]
    [InlineData("super_admin")]
    public void Diagnostics_pharmacy_and_platform_admins_never_receive_the_photo(string role) =>
        ProfilePhotoAccess.MayView([role]).Should().BeFalse();

    [Theory]
    [InlineData("reception")]
    [InlineData("call_center")]
    [InlineData("doctor")]
    [InlineData("beneficiary_mgmt")]
    public void Roles_with_an_identification_need_receive_the_photo(string role) =>
        ProfilePhotoAccess.MayView([role]).Should().BeTrue();

    [Fact]
    public void A_lab_sees_only_its_own_orders_and_nothing_else()
    {
        var lab = Ctx("lab_tech", providerId: "prov-1");
        ProfilePolicies.Decide(ProfileSections.Investigations, lab)!.Variant
            .Should().Be(ProfileVariants.OwnOrders);

        // Everything except identity, allergies and its own orders is absent for a lab.
        var served = ProfilePolicies.DecideAll(lab).Select(d => d.Key);
        served.Should().BeEquivalentTo([
            ProfileSections.Header, ProfileSections.Alerts, ProfileSections.Investigations]);
    }

    [Fact]
    public void A_pharmacy_sees_only_its_own_prescriptions_and_never_a_result()
    {
        var pharmacy = Ctx("pharmacist", providerId: "prov-2");
        ProfilePolicies.Decide(ProfileSections.Prescriptions, pharmacy)!.Variant
            .Should().Be(ProfileVariants.OwnRx);
        ProfilePolicies.Decide(ProfileSections.Investigations, pharmacy).Should().BeNull();
    }

    // ---------------------------------------------------------------- treating relationship

    [Theory]
    [InlineData(ProfileSections.PastMedicalHistory)]
    [InlineData(ProfileSections.Encounters)]
    [InlineData(ProfileSections.Investigations)]
    [InlineData(ProfileSections.Prescriptions)]
    [InlineData(ProfileSections.Documents)]
    [InlineData(ProfileSections.Notes)]
    public void A_treating_doctor_sees_the_clinical_sections(string section) =>
        StateOf("doctor", section, treating: true).Should().Be(ProfileSectionState.Visible);

    [Theory]
    [InlineData(ProfileSections.PastMedicalHistory)]
    [InlineData(ProfileSections.Encounters)]
    [InlineData(ProfileSections.Investigations)]
    [InlineData(ProfileSections.Prescriptions)]
    [InlineData(ProfileSections.Coverage)]
    [InlineData(ProfileSections.Timeline)]
    public void A_non_treating_doctor_gets_existence_only_with_a_reason(string section)
    {
        // Restricted, NOT absent. A doctor who sees nothing concludes the patient has no history; a doctor who
        // sees a locked card requests access. That difference is a clinical-safety property, not a UX one.
        var decision = ProfilePolicies.Decide(section, Ctx("doctor"))!;
        decision.State.Should().Be(ProfileSectionState.Restricted);
        decision.ReasonCode.Should().Be(ProfileReasons.NotTreating);
        decision.ShouldFetch.Should().BeFalse();
    }

    [Fact]
    public void A_non_treating_doctor_still_sees_identity_and_alerts()
    {
        // Unconditional on purpose: an allergy the treating check hides is an allergy nobody acts on.
        StateOf("doctor", ProfileSections.Header).Should().Be(ProfileSectionState.Visible);
        StateOf("doctor", ProfileSections.Alerts).Should().Be(ProfileSectionState.Visible);
    }

    [Fact]
    public void A_clinician_never_receives_the_financial_section()
    {
        ProfilePolicies.Decide(ProfileSections.Financial, Ctx("doctor", treating: true)).Should().BeNull();
        ProfilePolicies.Decide(ProfileSections.Financial, Ctx("nurse", treating: true)).Should().BeNull();
    }

    // ---------------------------------------------------------------- case assignment

    [Fact]
    public void An_unassigned_case_manager_gets_existence_only()
    {
        var decision = ProfilePolicies.Decide(ProfileSections.CaseManagement, Ctx("case_manager"))!;
        decision.State.Should().Be(ProfileSectionState.Restricted);
        decision.ReasonCode.Should().Be(ProfileReasons.NotAssigned);
    }

    [Fact]
    public void An_assigned_case_manager_coordinates_but_does_not_read_results()
    {
        var assigned = Ctx("case_manager", assigned: true);
        ProfilePolicies.Decide(ProfileSections.CaseManagement, assigned)!.State
            .Should().Be(ProfileSectionState.Visible);
        // Coordination needs to know a test HAPPENED, not what it said (design 39 §4).
        ProfilePolicies.Decide(ProfileSections.Investigations, assigned)!.State
            .Should().Be(ProfileSectionState.Restricted);
        ProfilePolicies.Decide(ProfileSections.Prescriptions, assigned)!.State
            .Should().Be(ProfileSectionState.Restricted);
    }

    // ---------------------------------------------------------------- the sensitive gate

    [Fact]
    public void The_approval_team_still_needs_a_grant_for_sensitive_results()
    {
        // Design 39 §4 note *. The approval team's standing oversight reaches the investigations SECTION, but the
        // profile hands the owning service the same "no grant" fact it would get on a direct read — so a
        // mental-health result stays existence-only here exactly as it does everywhere else.
        StateOf("medical_approval", ProfileSections.Investigations).Should().Be(ProfileSectionState.Visible);
        ProfilePolicies.SensitiveResultsExistenceOnly(Ctx("medical_approval")).Should().BeTrue();
        ProfilePolicies.SensitiveResultsExistenceOnly(Ctx("medical_director")).Should().BeTrue();
    }

    [Fact]
    public void A_held_grant_releases_the_sensitive_gate_and_nothing_else() =>
        ProfilePolicies.SensitiveResultsExistenceOnly(Ctx("medical_approval", grant: true)).Should().BeFalse();

    // ---------------------------------------------------------------- call history levels

    [Theory]
    [InlineData("call_center", CallHistoryLevel.Full)]
    [InlineData("medical_director", CallHistoryLevel.Full)]
    [InlineData("beneficiary_mgmt", CallHistoryLevel.Full)]
    [InlineData("reception", CallHistoryLevel.Operational)]
    [InlineData("medical_approval", CallHistoryLevel.Operational)]
    [InlineData("finance", CallHistoryLevel.Meta)]
    [InlineData("claims_officer", CallHistoryLevel.Meta)]
    [InlineData("lab_tech", CallHistoryLevel.None)]
    public void Call_history_projects_at_the_level_design_39_5b_names(string role, CallHistoryLevel expected) =>
        ProfilePolicies.CallHistoryLevelFor(Ctx(role)).Should().Be(expected);

    [Fact]
    public void An_assigned_case_manager_gets_full_call_history_an_unassigned_one_gets_none()
    {
        ProfilePolicies.CallHistoryLevelFor(Ctx("case_manager", assigned: true)).Should().Be(CallHistoryLevel.Full);
        ProfilePolicies.CallHistoryLevelFor(Ctx("case_manager")).Should().Be(CallHistoryLevel.None);
    }

    [Fact]
    public void A_non_treating_doctor_gets_existence_only_call_history() =>
        ProfilePolicies.CallHistoryLevelFor(Ctx("doctor")).Should().Be(CallHistoryLevel.None);

    [Theory]
    [InlineData(CallHistoryLevel.Full, CallHistoryLevel.Meta, CallHistoryLevel.Meta)]
    [InlineData(CallHistoryLevel.Full, CallHistoryLevel.Operational, CallHistoryLevel.Operational)]
    [InlineData(CallHistoryLevel.Meta, CallHistoryLevel.Full, CallHistoryLevel.Meta)]
    [InlineData(CallHistoryLevel.Operational, CallHistoryLevel.Operational, CallHistoryLevel.Operational)]
    public void A_client_supplied_level_may_narrow_but_never_widen(
        CallHistoryLevel requested, CallHistoryLevel allowed, CallHistoryLevel expected) =>
        ProfilePolicies.Clamp(requested, allowed).Should().Be(expected);

    // ---------------------------------------------------------------- multi-role resolution

    [Fact]
    public void A_principal_holding_two_roles_gets_the_widest_cell_but_not_a_widened_gate()
    {
        // A medical director who also carries the finance role: the widest CALL-HISTORY cell (Full, from the
        // director row) wins over Meta. That is ordinary RBAC union over grants.
        var both = new ProfileContext
        {
            Roles = new HashSet<string>(StringComparer.Ordinal) { "medical_director", "finance" },
        };
        ProfilePolicies.CallHistoryLevelFor(both).Should().Be(CallHistoryLevel.Full);

        // But a doctor who also carries reception does NOT get clinical sections without treating — the winning
        // cell still has to satisfy its own condition. This is design 39 §7.3: intersection, never union.
        var doctorReception = new ProfileContext
        {
            Roles = new HashSet<string>(StringComparer.Ordinal) { "doctor", "reception" },
        };
        ProfilePolicies.Decide(ProfileSections.PastMedicalHistory, doctorReception)!.State
            .Should().Be(ProfileSectionState.Restricted);
    }

    // ---------------------------------------------------------------- shape of the contract

    [Fact]
    public void There_are_exactly_fifteen_sections_in_design_39_order()
    {
        ProfileSections.All.Should().HaveCount(15);
        ProfileSections.All.Should().OnlyHaveUniqueItems();
        ProfileSections.All[0].Should().Be(ProfileSections.Header);
        ProfileSections.All[1].Should().Be(ProfileSections.Alerts, "alerts are pinned directly under the header");
        ProfileSections.All[^1].Should().Be(ProfileSections.CallHistory);
    }

    [Fact]
    public void Every_matrix_cell_names_a_known_section()
    {
        // Guards the one typo that would silently disable a rule: a cell keyed on "callhistory" is not a leak,
        // but it is a section nobody is ever served and nobody notices is missing.
        foreach (var role in ProfilePolicies.KnownRoles)
        {
            var ctx = new ProfileContext { Roles = new HashSet<string>(StringComparer.Ordinal) { role } };
            foreach (var decision in ProfilePolicies.DecideAll(ctx))
                ProfileSections.IsKnown(decision.Key).Should().BeTrue("'{0}' is not a section key", decision.Key);
        }
    }

    [Fact]
    public void An_unknown_role_is_served_nothing()
    {
        // Default-deny reaches the matrix too: a role added to identity but not to design 39 §4 gets an empty
        // profile, not a permissive one.
        var ctx = new ProfileContext { Roles = new HashSet<string>(StringComparer.Ordinal) { "some_new_role" } };
        ProfilePolicies.DecideAll(ctx).Should().BeEmpty();
    }

    [Fact]
    public void Only_call_centre_principals_face_the_verification_gate()
    {
        ProfilePolicies.RequiresCallCentreVerification(
            new HashSet<string>(StringComparer.Ordinal) { "call_center" }).Should().BeTrue();
        ProfilePolicies.RequiresCallCentreVerification(
            new HashSet<string>(StringComparer.Ordinal) { "doctor" }).Should().BeFalse();
    }

    [Fact]
    public void The_context_bar_asks_for_header_and_alerts_only() =>
        ProfileSections.ContextBar.Should().BeEquivalentTo([ProfileSections.Header, ProfileSections.Alerts]);
}
