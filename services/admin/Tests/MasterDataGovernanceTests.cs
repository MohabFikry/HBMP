using FluentAssertions;
using Mersal.Admin.Domain;
using Mersal.Authz;

namespace Mersal.Admin.Tests;

/// <summary>
/// How far the clinical-governance grant on master data reaches (ADR-0035 §4).
/// </summary>
/// <remarks>
/// <para>
/// The Medical Director holds <c>admin:edit-masterdata</c> on one argument: <b>they absorb the consequence of
/// getting it wrong.</b> A mis-mapped ICD code misroutes a diagnosis into their own approval queue; a wrong ATC
/// entry breaks the interaction check their reviewers rely on. That argument reaches the clinical vocabularies
/// and stops there — a director does not live with the consequence of a wrong formulary tier the same way, and
/// "they can already edit some of it" is not a reason to hand over the rest.
/// </para>
/// <para>
/// This is a SECOND, narrower question. Whether the caller may edit master data at all was already decided by
/// the ABAC gate, and these tests deliberately do not re-answer it: the same decision in two places is how the
/// two places come to disagree.
/// </para>
/// </remarks>
public class MasterDataGovernanceTests
{
    private static IReadOnlySet<string> Roles(params string[] r) =>
        new HashSet<string>(r, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(CodeSystem.Icd10)]
    [InlineData(CodeSystem.Cpt)]
    [InlineData(CodeSystem.Loinc)]
    [InlineData(CodeSystem.Atc)]
    public void Clinical_governance_edits_the_clinical_vocabularies(CodeSystem system)
    {
        MasterDataGovernance.MayEdit(Roles("medical_director"), system).Should().BeTrue();
    }

    [Theory]
    [InlineData(CodeSystem.Drug)]
    [InlineData(CodeSystem.DrugInteraction)]
    [InlineData(CodeSystem.Allergen)]
    [InlineData(CodeSystem.Formulary)]
    public void Clinical_governance_does_not_reach_the_administrative_ones(CodeSystem system)
    {
        MasterDataGovernance.MayEdit(Roles("medical_director"), system).Should().BeFalse(
            "the grant was made for a clinical reason and does not carry administrative reach with it");
    }

    [Theory]
    [InlineData(CodeSystem.Icd10)]
    [InlineData(CodeSystem.Formulary)]
    [InlineData(CodeSystem.Allergen)]
    public void Super_admin_keeps_every_system(CodeSystem system)
    {
        // This bound NARROWS nobody's existing access. It exists so a role granted the action for clinical
        // reasons cannot quietly acquire administrative reach with it.
        MasterDataGovernance.MayEdit(Roles("super_admin"), system).Should().BeTrue();
    }

    [Fact]
    public void An_unbounded_role_anywhere_in_the_set_wins()
    {
        // A principal may hold several roles. Holding super_admin AS WELL AS medical_director is more
        // authority, never less — an implementation that asked about "the role" would answer differently
        // depending on which one it happened to look at.
        MasterDataGovernance.MayEdit(Roles("medical_director", "super_admin"), CodeSystem.Formulary)
            .Should().BeTrue();
    }

    [Fact]
    public void A_role_this_bound_says_nothing_about_is_left_to_the_gate()
    {
        // org_admin is not named here, and must not be refused here. Whether it may edit master data at all is
        // the ABAC gate's decision; this check only narrows roles it explicitly bounds. Refusing an unknown
        // role would be this file quietly becoming a second authorization system.
        MasterDataGovernance.MayEdit(Roles("org_admin"), CodeSystem.Formulary).Should().BeTrue();
        MasterDataGovernance.MayEdit(null, CodeSystem.Formulary).Should().BeTrue();
        MasterDataGovernance.MayEdit(Roles(), CodeSystem.Formulary).Should().BeTrue();
    }

    [Fact]
    public void The_clinical_set_is_exactly_the_four_vocabularies()
    {
        // Pinned so a fifth cannot be added without someone stating why it belongs. Every entry here is a
        // vocabulary a clinician reads: a diagnosis, a procedure, a lab analyte, a drug class.
        MasterDataGovernance.ClinicalSystems.Should().BeEquivalentTo(
            new[] { CodeSystem.Icd10, CodeSystem.Cpt, CodeSystem.Loinc, CodeSystem.Atc });
    }

    // ---- the read behind the editor ------------------------------------------------------------

    [Fact]
    public void Whoever_may_EDIT_master_data_may_also_READ_it()
    {
        // The bug this pins. ADR-0035 §4 gave clinical governance the editor while the list behind it still
        // answered to `ReadAccess`, which is org_admin/super_admin only — an editor over a list its own author
        // could not open. Granting the write and forgetting the read is the same defect as granting the
        // authority and giving it no door, which is what that ADR set out to fix.
        var rules = AdminPolicies.Rules();
        var editors = rules.Single(r => r.Action == AdminPolicies.EditMasterData).Roles;
        var readers = rules.Single(r => r.Action == AdminPolicies.ReadMasterData).Roles;

        editors.Should().BeSubsetOf(readers);
    }

    [Fact]
    public void Reading_master_data_does_NOT_grant_the_platform_admin_reads()
    {
        // `ReadAccess` also gates the access matrix, the SoD matrix, break-glass and the access-review
        // campaigns. A Medical Director reading the ICD table is not one reading who can do what on the
        // platform, and a single action covering both would have to be wrong about one of those audiences.
        var rules = AdminPolicies.Rules();
        var readAccess = rules.Single(r => r.Action == AdminPolicies.ReadAccess).Roles;

        readAccess.Should().NotContain("medical_director");
    }
}
