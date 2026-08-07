using FluentAssertions;

namespace Mersal.Validity.Tests;

/// <summary>
/// How long a document is good for, and how early its lapse is warned about (ADR-0035 §6).
/// </summary>
/// <remarks>
/// <para>
/// The value being replaced is <c>PractitionerLicence.WarningDays = [90, 60, 30]</c> — a hard-coded constant,
/// which meant the one number a supervisor most obviously owns was the one they could not touch. Moving it to
/// configuration must change WHO may set it and change nothing about what happens by default: a migration
/// that also moved the behaviour would make any later surprise impossible to attribute.
/// </para>
/// <para>
/// The fallback discipline is the safety property, and it is the same one <see cref="ValidityPolicy"/> holds:
/// every failure path lands on a real number, never on "no expiry". A document that can never lapse is the
/// state this feature exists to prevent, so a malformed row, a missing key or a new tenant must not be able
/// to produce one.
/// </para>
/// </remarks>
public class DocumentValidityPolicyTests
{
    [Fact]
    public void The_default_thresholds_are_exactly_the_constant_they_replace()
    {
        // If this ever disagrees with PractitionerLicence.WarningDays, moving to configuration silently
        // changed when people are warned about an expiring licence.
        DocumentValidityPolicy.DefaultWarnDays.Should().Equal(90, 60, 30);
    }

    [Fact]
    public void Every_kind_has_a_distinct_key_for_both_numbers()
    {
        var keys = DocumentValidityPolicy.All
            .SelectMany(k => new[] { DocumentValidityPolicy.KeyFor(k), DocumentValidityPolicy.WarnKeyFor(k) })
            .ToList();

        // A collision would make two kinds share one setting, and the second would silently overwrite the
        // first with nothing on screen to say so.
        keys.Should().OnlyHaveUniqueItems();
        keys.Should().AllSatisfy(k => k.Should().StartWith("document-validity."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]          // below the floor
    [InlineData("-30")]
    [InlineData("36500")]      // above the ceiling
    public void An_unusable_cadence_falls_back_rather_than_throwing_or_unbounding(string? stored)
    {
        // A malformed row is an operator error. It must not stop a clerk registering somebody, and it must
        // not quietly grant a document a century of validity either.
        DocumentValidityPolicy.DaysFrom(stored).Should().Be(DocumentValidityPolicy.DefaultDays);
    }

    [Fact]
    public void A_cadence_inside_the_bounds_is_honoured()
    {
        DocumentValidityPolicy.DaysFrom("730").Should().Be(730);
        DocumentValidityPolicy.DaysFrom(DocumentValidityPolicy.MaxDays.ToString()).Should().Be(3650);
        DocumentValidityPolicy.DaysFrom("1").Should().Be(1);
    }

    [Fact]
    public void Thresholds_are_sorted_and_deduplicated()
    {
        // "30,90,30" means what its author obviously meant, and does not fire twice at the same point.
        DocumentValidityPolicy.WarnDaysFrom("30,90,30").Should().Equal(90, 30);
    }

    [Fact]
    public void An_out_of_range_threshold_is_dropped_without_losing_the_rest()
    {
        DocumentValidityPolicy.WarnDaysFrom("90,0,60,99999,30").Should().Equal(90, 60, 30);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("garbage")]
    [InlineData("0,-5,99999")]
    public void A_threshold_list_that_empties_out_falls_back_whole(string stored)
    {
        // "Warn at no point" must not be reachable by typing a bad number. A credential that goes silent is
        // worse than the constant this replaced.
        DocumentValidityPolicy.WarnDaysFrom(stored).Should().Equal(90, 60, 30);
    }

    [Fact]
    public void A_stored_list_round_trips()
    {
        var value = DocumentValidityPolicy.WarnDaysToValue([30, 120, 60]);
        value.Should().Be("120,60,30");
        DocumentValidityPolicy.WarnDaysFrom(value).Should().Equal(120, 60, 30);
    }

    [Fact]
    public void No_recorded_expiry_is_UNKNOWN_and_not_valid()
    {
        // The rule this platform holds everywhere: an absent answer is never rendered as a clean one. A
        // document with no expiry is one nobody has told us about, not one that never expires.
        DocumentValidityPolicy.DaysUntil(null, new DateOnly(2026, 8, 5)).Should().BeNull();
        DocumentValidityPolicy.ThresholdCrossedOn(null, new DateOnly(2026, 8, 5), [90, 60, 30]).Should().BeNull();
    }

    [Fact]
    public void A_threshold_fires_on_the_day_it_is_crossed_and_not_after()
    {
        var asOf = new DateOnly(2026, 8, 5);

        DocumentValidityPolicy.ThresholdCrossedOn(asOf.AddDays(90), asOf, [90, 60, 30]).Should().Be(90);
        DocumentValidityPolicy.ThresholdCrossedOn(asOf.AddDays(60), asOf, [90, 60, 30]).Should().Be(60);

        // Not on the days between. A sweeper that fired every day for the remaining ninety is how a warning
        // system teaches people to ignore it.
        DocumentValidityPolicy.ThresholdCrossedOn(asOf.AddDays(89), asOf, [90, 60, 30]).Should().BeNull();
        DocumentValidityPolicy.ThresholdCrossedOn(asOf.AddDays(45), asOf, [90, 60, 30]).Should().BeNull();
    }

    [Fact]
    public void An_already_expired_document_crosses_no_threshold()
    {
        // Past expiry the answer is not "warn" — it is expired, which is a different state with a different
        // handling. Reporting a negative remainder as a threshold would re-warn about something already gone.
        var asOf = new DateOnly(2026, 8, 5);
        DocumentValidityPolicy.ThresholdCrossedOn(asOf.AddDays(-1), asOf, [90, 60, 30]).Should().BeNull();
        DocumentValidityPolicy.DaysUntil(asOf.AddDays(-3), asOf).Should().Be(-3);
    }

    [Fact]
    public void The_identity_kinds_are_the_ones_whose_lapse_stops_a_person_being_seen()
    {
        DocumentValidityPolicy.IdentityKinds.Should().BeEquivalentTo(new[]
        {
            DocumentKind.NationalId, DocumentKind.Passport, DocumentKind.RefugeeId, DocumentKind.UnhcrNo,
        });

        // A provider credential is not an identity document: its lapse should stop somebody PRACTISING, which
        // is a different consequence reached by a different path.
        DocumentValidityPolicy.IdentityKinds.Should().NotContain(DocumentKind.PractitionerLicence);
    }

    [Fact]
    public void A_derived_review_date_is_the_cadence_from_when_it_was_recorded()
    {
        DocumentValidityPolicy.DerivedReviewDate(new DateOnly(2026, 1, 1), 365)
            .Should().Be(new DateOnly(2027, 1, 1));
    }

    [Fact]
    public void Every_kind_is_covered_by_All()
    {
        // A kind missing from All never appears on the supervisor's screen, so its policy can never be set
        // and it silently runs on the default for ever.
        DocumentValidityPolicy.All.Should().BeEquivalentTo(Enum.GetValues<DocumentKind>());
    }

    [Fact]
    public void Every_identity_kind_names_an_identifier_type_that_actually_exists()
    {
        // The tie to the rows this policy governs. `patient.beneficiary_identifier.identifier_type` carries a
        // CHECK constraint listing exactly these; a kind naming anything else would give a supervisor a
        // setting that is configured, saved, audited — and matches no row in the database.
        var vocabulary = new[] { "NationalID", "Passport", "RefugeeID", "UNHCRNo", "MemberNo" };

        foreach (var kind in DocumentValidityPolicy.IdentityKinds)
        {
            var type = DocumentValidityPolicy.IdentifierTypeFor(kind);
            type.Should().NotBeNull($"{kind} is an identity kind and must name an identifier_type");
            vocabulary.Should().Contain(type!);
        }
    }

    [Fact]
    public void A_provider_credential_names_no_identifier_type()
    {
        // A licence is not a beneficiary identifier. Mapping it to one would apply an identity policy to a
        // credential, which fails in a different direction entirely.
        DocumentValidityPolicy.IdentifierTypeFor(DocumentKind.PractitionerLicence).Should().BeNull();
    }

    [Theory]
    [InlineData("RefugeeID", DocumentKind.RefugeeId)]
    [InlineData("refugeeid", DocumentKind.RefugeeId)]
    [InlineData("NationalID", DocumentKind.NationalId)]
    [InlineData("UNHCRNo", DocumentKind.UnhcrNo)]
    public void An_identifier_type_resolves_to_its_kind(string type, DocumentKind expected)
    {
        DocumentValidityPolicy.KindForIdentifierType(type).Should().Be(expected);
    }

    [Theory]
    [InlineData("MemberNo")]
    [InlineData("SomethingElse")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ungoverned_identifier_type_resolves_to_NOTHING_rather_than_the_first_kind(string? type)
    {
        // The bug a search would have: `FirstOrDefault` over an enum returns the enum's DEFAULT when nothing
        // matches, so an unknown type would silently be governed by NationalId's policy. Null says "no policy
        // governs this", which is the truth and is actionable.
        DocumentValidityPolicy.KindForIdentifierType(type).Should().BeNull();
    }
}
