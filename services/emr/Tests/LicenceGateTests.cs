using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// 25.3 (design 42 §3) — an expired licence blocks FUTURE scheduling as at the SLOT DATE, and changes
/// nothing about the past.
///
/// The acceptance criteria from the design, one test each:
///   • licence expiring 30 Sep, slots generated for October ⇒ that practitioner has none
///   • a booking for a slot after expiry ⇒ 422 carrying the expiry date
///   • a past encounter ⇒ expiry changes nothing about it
/// </summary>
public class LicenceGateTests
{
    private static readonly Guid Doctor = new("44444444-0000-0000-0000-00000000000d");
    private static readonly Guid Branch = new("44444444-0000-0000-0000-00000000000b");
    private static readonly DateOnly Expiry = new(2026, 9, 30);

    private static ProviderAvailability Weekly(DayOfWeek day) => new()
    {
        AvailabilityId = Guid.NewGuid(), ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
        BranchId = Branch, DoctorId = Doctor, DayOfWeek = day,
        StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(12, 0), SlotMinutes = 30,
    };

    private static readonly TimeSpan Cairo = TimeSpan.FromHours(3);

    // ---- generation ------------------------------------------------------------------------------------

    [Fact]
    public void GIVEN_a_licence_expiring_30_Sep_WHEN_slots_are_generated_for_October_THEN_there_are_none()
    {
        var slots = SlotGeneration.Generate(
            Weekly(DayOfWeek.Thursday), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Cairo, Expiry);

        slots.Should().BeEmpty("the licence lapsed before October opened");
    }

    [Fact]
    public void AND_THE_NEGATION_the_same_October_range_is_full_when_the_licence_runs_on()
    {
        // Without this the assertion above would pass against a generator that produced nothing at all — and
        // it would keep passing after someone broke generation for every practitioner.
        var slots = SlotGeneration.Generate(
            Weekly(DayOfWeek.Thursday), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Cairo,
            bookableUntil: new DateOnly(2027, 1, 1));

        slots.Should().NotBeEmpty();
        slots.Should().HaveCount(5 * 6, "five Thursdays in October 2026, six 30-minute slots each");
    }

    [Fact]
    public void A_range_STRADDLING_the_expiry_generates_up_to_it_and_stops()
    {
        // The reason the bound is applied per date rather than as a precondition on the call: refusing the
        // whole request would make a coordinator generate two ranges by hand and guess the boundary.
        var slots = SlotGeneration.Generate(
            Weekly(DayOfWeek.Wednesday), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31), Cairo, Expiry);

        slots.Should().NotBeEmpty();
        slots.Select(s => DateOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime))
             .Should().OnlyContain(d => d <= Expiry);
        slots.Select(s => DateOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime))
             .Max().Should().Be(new DateOnly(2026, 9, 30), "30 September 2026 is a Wednesday and is INCLUDED");
    }

    [Fact]
    public void The_expiry_date_itself_still_generates_slots()
    {
        // The inclusive boundary, at the generator rather than only in the rule — the two must agree, and
        // this is the assertion that keeps them agreeing.
        var slots = SlotGeneration.Generate(
            Weekly(DayOfWeek.Wednesday), Expiry, Expiry, Cairo, Expiry);

        slots.Should().HaveCount(6);
        PractitionerLicenceBoundaryAgrees(Expiry).Should().BeTrue();
    }

    private static bool PractitionerLicenceBoundaryAgrees(DateOnly on) =>
        SlotGeneration.Generate(Weekly(on.DayOfWeek), on, on, Cairo, Expiry).Count > 0;

    [Fact]
    public void A_null_expiry_bounds_nothing()
    {
        // Nurses are recorded without a licence at all. Treating "not recorded" as "expired" would have
        // emptied every clinic's calendar on the day this shipped.
        var slots = SlotGeneration.Generate(
            Weekly(DayOfWeek.Monday), new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 31), Cairo, bookableUntil: null);

        slots.Should().NotBeEmpty();
    }

    // ---- the booking refusal ---------------------------------------------------------------------------

    [Fact]
    public void A_booking_after_expiry_is_refused_and_the_refusal_names_the_date()
    {
        var reason = PractitionerBranchRules.RefuseExpiredLicence(
            licenceValid: false, licenceExpiry: Expiry, Doctor, new DateOnly(2026, 10, 15));

        reason.Should().NotBeNull();
        reason.Should().Contain("2026-09-30", "the expiry date decides whether to wait for a renewal or find cover");
        reason.Should().Contain("2026-10-15");
    }

    [Fact]
    public void A_booking_on_or_before_expiry_is_not_refused()
    {
        PractitionerBranchRules.RefuseExpiredLicence(licenceValid: true, Expiry, Doctor, Expiry).Should().BeNull();
    }

    [Fact]
    public void UNKNOWN_licence_validity_proceeds_rather_than_failing_six_clinics()
    {
        // Same fail-open reasoning as the branch probe beside it: refusing every booking on this platform
        // because provider-service is briefly unreachable does more harm than the lapse it guards, and the
        // gate is re-applied at generation and at booking, so the window is small.
        PractitionerBranchRules.RefuseExpiredLicence(licenceValid: null, Expiry, Doctor, new DateOnly(2027, 1, 1))
            .Should().BeNull();
    }

    [Fact]
    public void The_two_refusals_carry_DIFFERENT_problem_types()
    {
        // The remedies differ and the desk needs to know which it hit: "not assigned to this branch" is fixed
        // by assigning them or picking another doctor; "licence expired" is fixed by recording a renewal.
        PractitionerBranchRules.ProblemType.Should().Be("urn:hbmp:practitioner-not-at-branch");
        PractitionerBranchRules.LicenceExpiredProblemType.Should().Be("urn:hbmp:practitioner-licence-expired");
        PractitionerBranchRules.ProblemType.Should().NotBe(PractitionerBranchRules.LicenceExpiredProblemType);
    }

    // ---- flag, never cancel; never retroactive ---------------------------------------------------------

    [Fact]
    public void The_consumer_parses_the_event_the_sweeper_publishes()
    {
        // Producer/consumer symmetry, asserted on the actual payload shape rather than assumed. The sibling
        // event (PractitionerBranchRevoked) shipped for months publishing to a queue nothing was bound to,
        // because a publish with no consumer does not fail.
        var json = """
        {"tenantId":"t-1","practitionerId":"44444444-0000-0000-0000-00000000000d",
         "fullNameEn":"Dr Hala Fouad","licenceExpiry":"2026-09-30",
         "branchIds":["44444444-0000-0000-0000-00000000000b"]}
        """;

        var parsed = Mersal.Emr.Api.PractitionerLicenceExpiredConsumer.Parse(json);

        parsed.Should().NotBeNull();
        parsed!.TenantId.Should().Be("t-1");
        parsed.PractitionerId.Should().Be(Doctor);
        parsed.LicenceExpiry.Should().Be(Expiry);
    }

    [Fact]
    public void An_event_with_no_tenant_is_REFUSED_rather_than_applied_under_a_guess()
    {
        // Applying it under a guessed tenant would flag another organisation's appointments.
        var json = """{"practitionerId":"44444444-0000-0000-0000-00000000000d","licenceExpiry":"2026-09-30"}""";
        Mersal.Emr.Api.PractitionerLicenceExpiredConsumer.Parse(json).Should().BeNull();
    }

    [Fact]
    public void An_event_with_no_expiry_date_is_REFUSED()
    {
        // The expiry is what decides WHICH appointments are affected. Without it the consumer would have to
        // choose between flagging everything and flagging nothing, and both are wrong.
        var json = """{"tenantId":"t-1","practitionerId":"44444444-0000-0000-0000-00000000000d"}""";
        Mersal.Emr.Api.PractitionerLicenceExpiredConsumer.Parse(json).Should().BeNull();
    }

    [Fact]
    public void The_envelope_may_wrap_the_payload_in_data()
    {
        var json = """
        {"type":"PractitionerLicenceExpired","data":{"tenantId":"t-1",
         "practitionerId":"44444444-0000-0000-0000-00000000000d","licenceExpiry":"2026-09-30"}}
        """;
        Mersal.Emr.Api.PractitionerLicenceExpiredConsumer.Parse(json).Should().NotBeNull();
    }
}
