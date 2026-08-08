using FluentAssertions;
using Mersal.Emr.Api;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// 25.4 (design 42 §4/§7 rule 5) — availability is computed in EXACTLY ONE place:
///
/// <code>recurring rule − exceptions ∩ active branch assignment ∩ valid licence ∩ practitioner Active</code>
///
/// Every one of these drives <see cref="SlotGeneration.Generate"/>, the same function the doctor picker,
/// <c>GET /booking/doctor-availability</c>, <c>GET /appointment-days</c>, slot materialization and the
/// booking validator all resolve through. If a second implementation appears, these tests keep passing and
/// the platform is still broken — which is why the ONE-PLACE rule is also asserted structurally at the end.
/// </summary>
public class RosterExceptionTests
{
    private static readonly Guid Maadi = new("55555555-0000-0000-0000-00000000000d");
    private static readonly Guid Dokki = new("55555555-0000-0000-0000-00000000000e");
    private static readonly Guid Hala = new("55555555-0000-0000-0000-0000000000aa");
    private static readonly Guid Omar = new("55555555-0000-0000-0000-0000000000bb");

    private static readonly TimeSpan Cairo = TimeSpan.FromHours(3);

    /// <summary>Tuesdays 09:00–12:00 at Maadi with Dr Hala — six 30-minute slots a day.</summary>
    private static ProviderAvailability TuesdayClinic() => new()
    {
        AvailabilityId = Guid.NewGuid(), ProviderId = Guid.NewGuid(), LocationId = Guid.NewGuid(),
        BranchId = Maadi, DoctorId = Hala, DayOfWeek = DayOfWeek.Tuesday,
        StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(12, 0), SlotMinutes = 30,
    };

    private static RosterException Ex(
        RosterExceptionKind kind, DateOnly from, DateOnly to,
        Guid? branch = null, Guid? practitioner = null,
        TimeOnly? start = null, TimeOnly? end = null) => new()
    {
        ExceptionId = Guid.NewGuid(), TenantId = "t-1",
        Kind = kind, DateFrom = from, DateTo = to,
        BranchId = branch, PractitionerId = practitioner,
        StartTime = start, EndTime = end, Reason = "test",
    };

    private static readonly DateOnly Sep1 = new(2026, 9, 1);    // a Tuesday
    private static readonly DateOnly Sep8 = new(2026, 9, 8);    // the next Tuesday
    private static readonly DateOnly Sep30 = new(2026, 9, 30);

    private static List<DateOnly> Days(IEnumerable<AppointmentSlot> slots) =>
        [.. slots.Select(s => DateOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime)).Distinct().Order()];

    private static IReadOnlyList<AppointmentSlot> Generate(params RosterException[] exceptions) =>
        SlotGeneration.Generate(TuesdayClinic(), Sep1, Sep30, Cairo, null, exceptions);

    // ---- the four kinds --------------------------------------------------------------------------------

    [Fact]
    public void GIVEN_leave_next_Tuesday_THEN_no_slots_that_day_AND_the_weekly_pattern_survives()
    {
        // The whole point of the exception layer. Before it, the only way to stop that Tuesday was to DELETE
        // the recurring rule — which also erased every other Tuesday, permanently, to cover one absence.
        var slots = Generate(Ex(RosterExceptionKind.Leave, Sep8, Sep8, practitioner: Hala));

        Days(slots).Should().NotContain(Sep8, "Dr Hala is on leave");
        Days(slots).Should().Contain(Sep1, "the weekly pattern is intact before it");
        Days(slots).Should().Contain(new DateOnly(2026, 9, 15), "and intact the following Tuesday");
        slots.Should().HaveCount(4 * 6, "five Tuesdays in September 2026, minus the one on leave");
    }

    [Fact]
    public void GIVEN_ClinicClosed_for_a_branch_THEN_no_practitioner_there_has_slots_that_day()
    {
        // A branch-targeted exception carries no practitioner, so it applies to everyone at that clinic.
        var slots = Generate(Ex(RosterExceptionKind.ClinicClosed, Sep8, Sep8, branch: Maadi));
        Days(slots).Should().NotContain(Sep8);
    }

    [Fact]
    public void A_closure_at_ANOTHER_branch_does_not_touch_this_one()
    {
        var slots = Generate(Ex(RosterExceptionKind.ClinicClosed, Sep8, Sep8, branch: Dokki));
        Days(slots).Should().Contain(Sep8, "Dokki closing is not Maadi's problem");
    }

    [Fact]
    public void Another_practitioners_leave_does_not_touch_this_one()
    {
        var slots = Generate(Ex(RosterExceptionKind.Leave, Sep8, Sep8, practitioner: Omar));
        Days(slots).Should().Contain(Sep8);
    }

    [Fact]
    public void A_public_holiday_removes_the_day_like_any_other_subtractive_kind()
    {
        Days(Generate(Ex(RosterExceptionKind.PublicHoliday, Sep8, Sep8, branch: Maadi))).Should().NotContain(Sep8);
    }

    [Fact]
    public void GIVEN_an_AdHocClinic_on_a_Friday_THEN_slots_appear_for_that_date_ONLY()
    {
        // The additive kind. A Friday the recurring rule says nothing about.
        var friday = new DateOnly(2026, 9, 4);
        var slots = Generate(Ex(RosterExceptionKind.AdHocClinic, friday, friday,
            branch: Maadi, practitioner: Hala, start: new TimeOnly(14, 0), end: new TimeOnly(16, 0)));

        Days(slots).Should().Contain(friday);
        Days(slots).Where(d => d.DayOfWeek == DayOfWeek.Friday).Should().ContainSingle(
            "only THAT Friday — an ad-hoc clinic is a date, not a new weekly pattern");
        slots.Count(s => DateOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime) == friday)
             .Should().Be(4, "14:00–16:00 in 30-minute slots");
    }

    // ---- whole-day vs part-day, and overlaps -----------------------------------------------------------

    [Fact]
    public void A_PART_DAY_leave_removes_only_the_slots_it_overlaps()
    {
        // 09:00–10:30 off: the 09:00, 09:30 and 10:00 slots go; 10:30, 11:00 and 11:30 stay.
        var slots = Generate(Ex(RosterExceptionKind.Leave, Sep8, Sep8,
            practitioner: Hala, start: new TimeOnly(9, 0), end: new TimeOnly(10, 30)));

        var onTheDay = slots
            .Where(s => DateOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime) == Sep8)
            .Select(s => TimeOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime))
            .Order().ToList();

        onTheDay.Should().Equal([new TimeOnly(10, 30), new TimeOnly(11, 0), new TimeOnly(11, 30)]);
    }

    [Fact]
    public void A_slot_that_only_PARTLY_overlaps_the_absence_is_still_removed()
    {
        // Overlap, not containment: a slot half inside a leave window is not half-bookable.
        var slots = Generate(Ex(RosterExceptionKind.Leave, Sep8, Sep8,
            practitioner: Hala, start: new TimeOnly(9, 15), end: new TimeOnly(9, 45)));

        var onTheDay = slots
            .Where(s => DateOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime) == Sep8)
            .Select(s => TimeOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime))
            .Order().ToList();

        onTheDay.Should().NotContain(new TimeOnly(9, 0), "09:00-09:30 overlaps 09:15-09:45");
        onTheDay.Should().NotContain(new TimeOnly(9, 30), "09:30-10:00 overlaps too");
        onTheDay.Should().Contain(new TimeOnly(10, 0));
    }

    [Fact]
    public void A_DATE_RANGE_removes_every_matching_day_in_it()
    {
        var slots = Generate(Ex(RosterExceptionKind.Leave, Sep1, new DateOnly(2026, 9, 20), practitioner: Hala));
        Days(slots).Should().Equal([new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 29)]);
    }

    [Fact]
    public void OVERLAPPING_exceptions_both_apply_and_neither_cancels_the_other()
    {
        // Two absences on one day. The union is removed; there is no precedence to get wrong.
        var slots = Generate(
            Ex(RosterExceptionKind.Leave, Sep8, Sep8, practitioner: Hala, start: new TimeOnly(9, 0), end: new TimeOnly(10, 0)),
            Ex(RosterExceptionKind.Leave, Sep8, Sep8, practitioner: Hala, start: new TimeOnly(11, 0), end: new TimeOnly(12, 0)));

        var onTheDay = slots
            .Where(s => DateOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime) == Sep8)
            .Select(s => TimeOnly.FromDateTime(s.SlotStart.ToOffset(Cairo).DateTime))
            .Order().ToList();

        onTheDay.Should().Equal([new TimeOnly(10, 0), new TimeOnly(10, 30)]);
    }

    [Fact]
    public void A_WHOLE_DAY_closure_beats_an_ad_hoc_clinic_on_the_same_day()
    {
        // Deliberate ordering: if the clinic is shut, an extra session at a shut clinic is not a session. The
        // alternative would let a stale ad-hoc row quietly reopen a branch somebody closed.
        var friday = new DateOnly(2026, 9, 4);
        var slots = Generate(
            Ex(RosterExceptionKind.AdHocClinic, friday, friday, branch: Maadi, practitioner: Hala,
               start: new TimeOnly(14, 0), end: new TimeOnly(16, 0)),
            Ex(RosterExceptionKind.ClinicClosed, friday, friday, branch: Maadi));

        Days(slots).Should().NotContain(friday);
    }

    // ---- the intersection with licence and assignment --------------------------------------------------

    [Fact]
    public void The_licence_bound_and_an_exception_compose()
    {
        var slots = SlotGeneration.Generate(
            TuesdayClinic(), Sep1, Sep30, Cairo,
            bookableUntil: new DateOnly(2026, 9, 16),
            exceptions: [Ex(RosterExceptionKind.Leave, Sep8, Sep8, practitioner: Hala)]);

        Days(slots).Should().Equal([Sep1, new DateOnly(2026, 9, 15)],
            "the 8th is leave; the 22nd and 29th are past the bound");
    }

    [Fact]
    public void BookableUntil_takes_the_EARLIER_of_the_licence_and_the_assignment()
    {
        // Combined in ONE place so neither call site can forget one of them. Forgetting the assignment bound
        // generates slots for a locum whose contract ended, which looks like a working calendar right up
        // until the patient arrives.
        var licence = new DateOnly(2026, 12, 31);
        var assignment = new DateOnly(2026, 10, 15);

        SlotGeneration.BookableUntil(licence, assignment).Should().Be(assignment);
        SlotGeneration.BookableUntil(assignment, licence).Should().Be(assignment);
        SlotGeneration.BookableUntil(licence, null).Should().Be(licence, "an open-ended assignment bounds nothing");
        SlotGeneration.BookableUntil(null, assignment).Should().Be(assignment);
        SlotGeneration.BookableUntil(null, null).Should().BeNull();
    }

    // ---- no exceptions changes nothing -----------------------------------------------------------------

    [Fact]
    public void Passing_NO_exceptions_reproduces_the_pre_25_4_behaviour_exactly()
    {
        var withNone = SlotGeneration.Generate(TuesdayClinic(), Sep1, Sep30, Cairo);
        var withEmpty = SlotGeneration.Generate(TuesdayClinic(), Sep1, Sep30, Cairo, null, []);

        withNone.Should().HaveCount(5 * 6);
        withEmpty.Should().HaveCount(withNone.Count);
    }

    [Fact]
    public void A_soft_deleted_exception_applies_to_nothing()
    {
        // Withdrawing a closure RESTORES availability. The row survives — it is the record of who closed the
        // clinic and why — but it stops biting.
        var withdrawn = Ex(RosterExceptionKind.ClinicClosed, Sep8, Sep8, branch: Maadi);
        withdrawn.IsDeleted = true;

        Days(Generate(withdrawn)).Should().Contain(Sep8);
    }

    // ---- the targeting rules ---------------------------------------------------------------------------

    [Fact]
    public void An_exception_naming_BOTH_targets_applies_only_to_that_pair()
    {
        // "Dr Hala is away FROM MAADI" (she is covering Dokki that day) must not close her Dokki clinic.
        var e = Ex(RosterExceptionKind.Leave, Sep8, Sep8, branch: Maadi, practitioner: Hala);

        e.AppliesTo(Sep8, Maadi, Hala).Should().BeTrue();
        e.AppliesTo(Sep8, Dokki, Hala).Should().BeFalse("a different clinic");
        e.AppliesTo(Sep8, Maadi, Omar).Should().BeFalse("a different clinician");
    }

    [Fact]
    public void A_branch_closure_does_NOT_match_a_branchless_availability_rule()
    {
        // Availability rows from before 14.4 carry no branch. Closing Maadi must not silently close a rule
        // whose branch nobody ever set — that would take clinics offline that are not the one being closed.
        Ex(RosterExceptionKind.ClinicClosed, Sep8, Sep8, branch: Maadi)
            .AppliesTo(Sep8, branchId: null, practitionerId: Hala).Should().BeFalse();
    }

    [Fact]
    public void Whole_day_is_null_start_AND_null_end()
    {
        Ex(RosterExceptionKind.Leave, Sep8, Sep8, practitioner: Hala).IsWholeDay.Should().BeTrue();
        Ex(RosterExceptionKind.Leave, Sep8, Sep8, practitioner: Hala,
           start: new TimeOnly(9, 0), end: new TimeOnly(10, 0)).IsWholeDay.Should().BeFalse();
    }

    [Fact]
    public void Only_AdHocClinic_is_additive()
    {
        Ex(RosterExceptionKind.AdHocClinic, Sep8, Sep8, branch: Maadi).IsSubtractive.Should().BeFalse();
        foreach (var k in new[] { RosterExceptionKind.Leave, RosterExceptionKind.PublicHoliday, RosterExceptionKind.ClinicClosed })
            Ex(k, Sep8, Sep8, branch: Maadi).IsSubtractive.Should().BeTrue();
    }

    // ---- request validation ----------------------------------------------------------------------------

    [Fact]
    public void A_reason_is_MANDATORY()
    {
        // A cancelled clinic day is something a patient will ask about, and "no reason recorded" is not an
        // answer anyone can give them.
        var req = new CreateRosterException("ClinicClosed", Sep8, Sep8, "  ", BranchId: Maadi);
        RosterExceptionRules.Validate(req)!.Type.Should().Be("urn:hbmp:reason-required");
    }

    [Fact]
    public void A_half_open_time_window_is_refused()
    {
        // "From 14:00, until unspecified" reads as a whole afternoon to one person and a data-entry slip to
        // another.
        var req = new CreateRosterException("Leave", Sep8, Sep8, "annual leave",
            PractitionerId: Hala, StartTime: new TimeOnly(14, 0));
        RosterExceptionRules.Validate(req)!.Type.Should().Be("urn:hbmp:invalid-window");
    }

    [Fact]
    public void An_ad_hoc_clinic_must_say_when_it_runs()
    {
        // There is no weekly pattern for it to inherit a window from.
        var req = new CreateRosterException("AdHocClinic", Sep8, Sep8, "extra Friday clinic", BranchId: Maadi);
        RosterExceptionRules.Validate(req)!.Type.Should().Be("urn:hbmp:invalid-window");
    }

    [Fact]
    public void An_inverted_date_range_is_refused()
    {
        var req = new CreateRosterException("Leave", Sep30, Sep1, "annual leave", PractitionerId: Hala);
        RosterExceptionRules.Validate(req)!.Type.Should().Be("urn:hbmp:invalid-range");
    }

    [Fact]
    public void An_unknown_kind_is_refused()
    {
        var req = new CreateRosterException("Holiday", Sep8, Sep8, "eid", BranchId: Maadi);
        RosterExceptionRules.Validate(req)!.Type.Should().Be("urn:hbmp:invalid-roster-kind");
    }

    [Fact]
    public void A_valid_request_passes()
    {
        // The negation: without it, a validator that refused everything would satisfy all five above.
        RosterExceptionRules.Validate(
            new CreateRosterException("Leave", Sep8, Sep8, "annual leave", PractitionerId: Hala))
            .Should().BeNull();
        RosterExceptionRules.Validate(
            new CreateRosterException("AdHocClinic", Sep8, Sep8, "extra clinic", BranchId: Maadi,
                StartTime: new TimeOnly(14, 0), EndTime: new TimeOnly(16, 0)))
            .Should().BeNull();
    }
}

/// <summary>
/// 25.4 / design 42 §7 rule 5, asserted STRUCTURALLY — availability is computed in exactly one place.
///
/// The behavioural tests above all drive <see cref="SlotGeneration.Generate"/>, so they would keep passing
/// if a second, divergent implementation appeared somewhere else and a screen started using it. That is the
/// failure the rule is actually about: the picker, the slot table and the booking validator disagreeing, and
/// a patient given an appointment with a doctor who is on leave.
/// </summary>
public class OneAvailabilityComputationTests
{
    [Fact]
    public void Nothing_outside_SlotGeneration_materializes_a_slot()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "services", "emr"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/Tests/", StringComparison.Ordinal)) continue;
            // The one place allowed to construct slots, plus the endpoint that persists what it returns.
            if (rel.EndsWith("Domain/SlotGeneration.cs", StringComparison.Ordinal)) continue;

            var text = File.ReadAllText(file);
            if (text.Contains("new AppointmentSlot", StringComparison.Ordinal))
                offenders.Add(rel);
        }

        offenders.Should().BeEmpty(
            "availability is computed in exactly one place (design 42 §7 rule 5). A second implementation is " +
            "the bug, not an optimisation — the way that failure presents is a patient given an appointment " +
            "with a doctor who is on leave. Offending files:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_scan_reads_a_plausible_tree()
    {
        // Guards the guard: a path typo would make the assertion above vacuously green.
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "services", "emr"), "*.cs", SearchOption.AllDirectories)
            .Should().HaveCountGreaterThan(20);
        File.ReadAllText(Path.Combine(RepoRoot(), "services", "emr", "Domain", "SlotGeneration.cs"))
            .Should().Contain("new AppointmentSlot", "the one allowed construction site must still be there");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
