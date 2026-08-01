using FluentAssertions;
using Mersal.Patient.Api;
using Mersal.Patient.Domain;

namespace Mersal.Patient.Tests;

/// <summary>
/// US-002 — correcting the identity record.
///
/// <para>Until now nothing could. A registration captures twenty-two fields transcribed from documents in a
/// second language at a busy desk; the only writes on a beneficiary were register (once), status (its own
/// transition table) and the bulk by-card upsert. An officer who mistyped a birth date had to ask for a
/// re-import of a file they may not have had.</para>
///
/// <para>The rules are pure so the decisions that matter are testable without a database: a future birth date
/// is refused, an unchanged value is not a change, and a partial update cannot blank the fields it omits.</para>
/// </summary>
public class BeneficiaryEditTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static Beneficiary Person() => new()
    {
        BeneficiaryId = Guid.NewGuid(),
        TenantId = "11111111-1111-1111-1111-111111111111",
        GivenName = "Amal",
        MiddleName = null,
        FamilyName = "Hassan",
        BirthDate = new DateOnly(1990, 3, 14),
        BirthDateIsApproximate = false,
        Sex = "Female",
        NationalityCode = "SD",
        IndividualNo = null,
        CaseNo = "CASE-1",
    };

    private static BeneficiaryEdit Edit(
        string? given = null, string? middle = null, string? family = null,
        DateOnly? dob = null, bool? approx = null, string? sex = null,
        string? nationality = null, string? individualNo = null, string? caseNo = null)
        => new(given, middle, family, dob, approx, sex, nationality, individualNo, caseNo);

    // ---- validation ----------------------------------------------------------------------------------------

    [Fact]
    public void Accepts_an_empty_request()
        // Every field absent is a valid "change nothing" — the shape a form sends when the operator opened it
        // and changed their mind.
        => BeneficiaryEditRules.Validate(Edit(), Now).Should().BeEmpty();

    [Fact]
    public void Refuses_a_future_birth_date()
        => BeneficiaryEditRules.Validate(Edit(dob: new DateOnly(2030, 1, 1)), Now).Should().Contain("birthDate");

    [Fact]
    public void Refuses_blanking_a_mandatory_name()
    {
        // PRESENT but blank is a mistake, not a clearing: an update that emptied one of these would leave a
        // person the directory cannot find.
        BeneficiaryEditRules.Validate(Edit(given: "   "), Now).Should().Contain("givenName");
        BeneficiaryEditRules.Validate(Edit(family: ""), Now).Should().Contain("familyName");
    }

    [Fact]
    public void Refuses_a_nationality_that_is_not_alpha_2()
    {
        // A three-letter code the column would accept and every report that joins on it would miss.
        BeneficiaryEditRules.Validate(Edit(nationality: "SDN"), Now).Should().Contain("nationalityCode");
        BeneficiaryEditRules.Validate(Edit(nationality: "S1"), Now).Should().Contain("nationalityCode");
        BeneficiaryEditRules.Validate(Edit(nationality: "sd"), Now).Should().BeEmpty();
    }

    [Fact]
    public void Refuses_a_sex_outside_the_vocabulary()
        => BeneficiaryEditRules.Validate(Edit(sex: "F"), Now).Should().Contain("sex");

    // ---- what actually changed -----------------------------------------------------------------------------

    [Fact]
    public void An_absent_field_is_left_alone()
    {
        // The whole point of a partial update: a form showing five fields cannot blank the four it did not.
        var b = Person();
        BeneficiaryEditRules.Apply(b, Edit(given: "Amal")).Should().BeEmpty("the value was already Amal");
        b.FamilyName.Should().Be("Hassan");
        b.CaseNo.Should().Be("CASE-1");
    }

    [Fact]
    public void An_unchanged_value_is_not_a_change()
    {
        // Otherwise the log fills with entries recording that somebody opened a form and pressed save, and
        // the one entry that matters becomes as hard to find as it was before there was a log.
        var b = Person();
        var changes = BeneficiaryEditRules.Apply(
            b, Edit(given: "Amal", family: "Hassan", dob: new DateOnly(1990, 3, 14), sex: "Female"));
        changes.Should().BeEmpty();
    }

    [Fact]
    public void Reports_each_field_that_moved_with_its_before_and_after()
    {
        var b = Person();
        var changes = BeneficiaryEditRules.Apply(b, Edit(given: "Amaal", dob: new DateOnly(1990, 3, 15)));

        changes.Should().HaveCount(2);
        changes.Should().ContainSingle(c => c.Field == "givenName" && c.Before == "Amal" && c.After == "Amaal");
        changes.Should().ContainSingle(c => c.Field == "birthDate" && c.Before == "1990-03-14" && c.After == "1990-03-15");
        b.GivenName.Should().Be("Amaal");
        b.BirthDate.Should().Be(new DateOnly(1990, 3, 15));
    }

    [Fact]
    public void Trims_on_the_way_in()
    {
        // A trailing space is not a correction anyone made on purpose, and it is the difference between two
        // rows that look identical in every report.
        var b = Person();
        BeneficiaryEditRules.Apply(b, Edit(given: " Amal ")).Should().BeEmpty();
        BeneficiaryEditRules.Apply(b, Edit(middle: "  Noor  ")).Should().ContainSingle(c => c.After == "Noor");
    }

    [Fact]
    public void An_optional_field_can_be_emptied_but_a_mandatory_one_cannot()
    {
        var b = Person();
        // Optional: an empty string clears it, and that is a recorded change.
        BeneficiaryEditRules.Apply(b, Edit(caseNo: "")).Should().ContainSingle(c => c.Field == "caseNo" && c.After == null);
        b.CaseNo.Should().BeNull();

        // Mandatory: validation refuses it before Apply is reached, and Apply itself will not write a null
        // over a required name even if it were.
        BeneficiaryEditRules.Apply(b, Edit(given: ""));
        b.GivenName.Should().Be("Amal");
    }

    [Fact]
    public void Stores_the_nationality_upper_case()
    {
        // So a lookup never depends on how it was typed.
        var b = Person();
        BeneficiaryEditRules.Apply(b, Edit(nationality: "eg")).Should().ContainSingle(c => c.After == "EG");
        b.NationalityCode.Should().Be("EG");
    }

    [Fact]
    public void The_approximate_flag_is_a_change_in_its_own_right()
    {
        // It travels WITH the date: a consumer that receives the date without the flag has no way to know it
        // is an estimate, which is how an estimated date becomes a hard eligibility cutoff.
        var b = Person();
        BeneficiaryEditRules.Apply(b, Edit(approx: true))
            .Should().ContainSingle(c => c.Field == "birthDateIsApproximate" && c.After == "True");
        b.BirthDateIsApproximate.Should().BeTrue();
    }

    // ---- the audit description -----------------------------------------------------------------------------

    [Fact]
    public void Describes_only_the_fields_that_moved()
    {
        // A diff of the whole record would bury one corrected letter in twenty unchanged fields; the audit
        // trail's job here is "what did they change", not "what does the row look like".
        var b = Person();
        var changes = BeneficiaryEditRules.Apply(b, Edit(given: "Amaal", caseNo: "CASE-2"));

        BeneficiaryEditRules.Describe(changes, before: true)
            .Should().Be("""{"givenName":"Amal","caseNo":"CASE-1"}""");
        BeneficiaryEditRules.Describe(changes, before: false)
            .Should().Be("""{"givenName":"Amaal","caseNo":"CASE-2"}""");
    }

    [Fact]
    public void Describes_a_cleared_field_as_null_not_as_an_empty_string()
    {
        var b = Person();
        var changes = BeneficiaryEditRules.Apply(b, Edit(caseNo: ""));
        BeneficiaryEditRules.Describe(changes, before: false).Should().Be("""{"caseNo":null}""");
    }
}
