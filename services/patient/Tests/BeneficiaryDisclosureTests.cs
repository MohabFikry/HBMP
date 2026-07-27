using FluentAssertions;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Patient.Tests;

/// <summary>
/// Phase 18.B3 (audit R2 S6) — what each role actually receives from a beneficiary read.
///
/// The record holds the identifier set: national ID, UNHCR registration number, passport. For a refugee
/// population that is the most dangerous data on the platform — a leaked UNHCR number is a person locatable
/// by an authority they fled — and it was returned in full to anyone holding <c>patient:write</c>, with no
/// engine call, no row scope, no audit and no projection.
///
/// These assert the projection matrix directly. The wiring that applies it lives in BeneficiaryReadGuard and
/// is covered by the endpoint's own integration test; what matters here is the RULE, because that is the part
/// a future change is most likely to get subtly wrong.
/// </summary>
public class BeneficiaryDisclosureTests
{
    private static IReadOnlySet<string> Roles(params string[] roles) =>
        new HashSet<string>(roles, StringComparer.Ordinal);

    private static IReadOnlySet<string> Readable(params string[] roles) =>
        PatientPolicies.FieldMatrix().ReadableClasses(Roles(roles));

    [Fact]
    public void Reception_gets_contact_details_and_never_the_identifier_values()
    {
        // The exact trade the pii/contact split exists to make: reception must be able to phone the member,
        // and must not be able to read the number that identifies them to a government.
        var classes = Readable("reception");
        classes.Should().Contain(DefaultPolicies.Classes.Contact, "reception has to reach the member");
        classes.Should().NotContain(DefaultPolicies.Classes.Pii,
            "a UNHCR registration number is not needed to check someone in at a desk");
    }

    [Fact]
    public void The_call_centre_gets_contact_only()
    {
        var classes = Readable("call_center");
        classes.Should().Contain(DefaultPolicies.Classes.Contact);
        classes.Should().NotContain(DefaultPolicies.Classes.Pii);
        classes.Should().NotContain(DefaultPolicies.Classes.Diagnosis, "the 360 view is clinical-free (Phase 15)");
    }

    [Fact]
    public void Registration_and_clinicians_may_verify_against_documents()
    {
        // beneficiary_mgmt registers a person against their papers, and a doctor confirms identity before
        // treating. Both need the values; neither gets them by default anywhere else.
        Readable("beneficiary_mgmt").Should().Contain(DefaultPolicies.Classes.Pii);
        Readable("doctor").Should().Contain(DefaultPolicies.Classes.Pii);
    }

    [Fact]
    public void Technicians_and_pharmacists_get_neither_pii_nor_contact()
    {
        // A lab technician confirming whose specimen this is needs the name and member number (baseline
        // identity), and nothing further.
        foreach (var role in new[] { "lab_tech", "imaging_tech", "pharmacist" })
        {
            Readable(role).Should().NotContain(DefaultPolicies.Classes.Pii, "{0} must not read identifier values", role);
            Readable(role).Should().NotContain(DefaultPolicies.Classes.Contact, "{0} must not read contacts", role);
        }
    }

    [Fact]
    public void Administrators_are_not_routine_readers_of_pii()
    {
        // 10-role-matrix §3.15/§3.16: an admin administers ACCESS, not content. PHI is break-glass only, and
        // break-glass widens the ABAC decision — it does not silently widen the field matrix.
        foreach (var role in new[] { "org_admin", "super_admin" })
        {
            Readable(role).Should().NotContain(DefaultPolicies.Classes.Pii, "{0} is not a routine PHI reader", role);
            Readable(role).Should().NotContain(DefaultPolicies.Classes.Contact, "{0} is not a routine PHI reader", role);
        }
    }

    [Fact]
    public void Every_reader_can_still_tell_which_person_they_have()
    {
        // The projection must not become unusable: name, member number and status stay in the baseline
        // identity class for everyone. Withholding those would just push staff to guess.
        foreach (var role in new[] { "reception", "call_center", "lab_tech", "pharmacist", "doctor", "org_admin" })
            Readable(role).Should().Contain(DefaultPolicies.Classes.Identity, "{0} must be able to identify the record", role);
    }

    [Fact]
    public void The_read_rule_is_sensitive_so_the_engine_audits_the_allow()
    {
        // §19 requires a PHI read to be audited. Marking the rule Sensitive is what makes a PERMITTED read
        // produce a record — a deny is always logged, an allow is the one that used to vanish.
        var rule = PatientPolicies.Rules().Single(r => r.Action == PatientPolicies.ReadBeneficiary);
        rule.Sensitive.Should().BeTrue();
        rule.RequiredConditions.Should().Contain(AbacConditions.TenantMatch, "a read must be row-scoped to the caller's tenant");
        rule.Scopes.Should().BeEquivalentTo(["patient:read"], "reading must not require the authority to rewrite");
    }

    [Fact]
    public void Claims_and_finance_cannot_read_the_beneficiary_directory_at_all()
    {
        // 11-permission-matrix §3.2. They work from a claim's beneficiary reference; the identifier set is not
        // part of adjudicating money, and the rule's role list is where that is enforced.
        var rule = PatientPolicies.Rules().Single(r => r.Action == PatientPolicies.ReadBeneficiary);
        rule.Roles.Should().NotContain("finance").And.NotContain("claims_officer");
    }
}
