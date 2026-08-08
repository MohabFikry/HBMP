using FluentAssertions;
using Mersal.Patient.Domain;

namespace Mersal.Patient.Tests;

public class RegistrationTests
{
    private sealed class FakeLookup(params (IdentifierType, string, Guid)[] existing) : IIdentifierLookup
    {
        private readonly Dictionary<(IdentifierType, string), Guid> _map =
            existing.ToDictionary(e => (e.Item1, IdentifierValidation.Normalize(e.Item2)), e => e.Item3);

        /// <summary>Cards already in issue, keyed by their NORMALIZED value.</summary>
        public Dictionary<string, Guid> Cards { get; } = new(StringComparer.Ordinal);

        public Task<Guid?> FindActiveOwnerAsync(IdentifierType type, string norm, CancellationToken ct = default) =>
            Task.FromResult(_map.TryGetValue((type, norm), out var id) ? id : (Guid?)null);

        public Task<Guid?> FindActiveCardHolderAsync(string card, CancellationToken ct = default) =>
            Task.FromResult(Cards.TryGetValue(card, out var id) ? id : (Guid?)null);
    }

    private static readonly NewEnrolmentIntent Intent =
        new(Guid.NewGuid(), Guid.NewGuid(), ContributionPercent: 20m, DefaultBranchId: null);

    /// <summary>A complete, valid request. Every test that is not ABOUT a missing field starts from one that
    /// would be accepted, so a failure names the rule under test rather than the setup.</summary>
    private static RegisterBeneficiaryRequest Req(params NewIdentifier[] ids) =>
        new("Amina", "Yusuf", new DateOnly(1990, 1, 1), "Female", "SY", ids, [])
        {
            CardNumber = "#A-1001",
            Enrolment = Intent,
        };

    [Fact]
    public async Task Valid_single_identifier_creates_pending_beneficiary()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(new NewIdentifier(IdentifierType.UNHCRNo, "123-45C67890", "SY", true)), "officer-1");

        var created = result.Should().BeOfType<RegistrationResult.Created>().Subject;
        created.Beneficiary.Status.Should().Be(BeneficiaryStatus.Pending);   // US-001: created Pending
        created.Beneficiary.Identifiers.Should().ContainSingle();
        created.Beneficiary.MemberNo.Should().BeNull();                      // issued only at activation
    }

    [Fact]
    public async Task Duplicate_active_identifier_returns_existing_id_not_a_new_row()
    {
        var existingId = Guid.NewGuid();
        var reg = new BeneficiaryRegistrar(new FakeLookup((IdentifierType.NationalID, "29001011234567", existingId)), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(new NewIdentifier(IdentifierType.NationalID, "29001011234567")), "officer-1");

        var dup = result.Should().BeOfType<RegistrationResult.DuplicateIdentifier>().Subject;
        dup.ExistingBeneficiaryId.Should().Be(existingId);   // US-001: name the existing record, no duplicate
    }

    [Fact]
    public async Task Duplicate_detection_is_normalization_insensitive()
    {
        var existingId = Guid.NewGuid();
        var reg = new BeneficiaryRegistrar(new FakeLookup((IdentifierType.Passport, "AB123456", existingId)), TimeProvider.System);

        // same value with spaces / lower-case → still a duplicate
        var result = await reg.RegisterAsync(Req(new NewIdentifier(IdentifierType.Passport, " ab 123456 ")), "o");
        result.Should().BeOfType<RegistrationResult.DuplicateIdentifier>();
    }

    [Fact]
    public async Task Missing_name_or_identifier_is_invalid_with_field_list()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(new RegisterBeneficiaryRequest("", "", null, null, null, [], []), "o");

        var invalid = result.Should().BeOfType<RegistrationResult.Invalid>().Subject;
        invalid.Errors.Should().Contain(e => e.Contains("givenName"))
            .And.Contain(e => e.Contains("familyName"))
            .And.Contain(e => e.Contains("identifier"));
    }

    [Theory]
    [InlineData(IdentifierType.NationalID, "12345")]       // not 14 digits
    [InlineData(IdentifierType.Passport, "AB")]            // too short
    [InlineData(IdentifierType.UNHCRNo, "!!")]             // bad chars
    public async Task Bad_identifier_format_is_rejected(IdentifierType type, string value)
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);
        var result = await reg.RegisterAsync(Req(new NewIdentifier(type, value)), "o");
        result.Should().BeOfType<RegistrationResult.Invalid>();
    }

    // ── The operational record ──────────────────────────────────────────────────────────────────────────

    private static NewIdentifier AnId() => new(IdentifierType.UNHCRNo, "123-45C67890", "SY", true);

    [Fact]
    public async Task Card_number_is_required()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);
        var req = Req(AnId()) with { };

        var result = await reg.RegisterAsync(req with { CardNumber = "" }, "o");

        result.Should().BeOfType<RegistrationResult.Invalid>()
            .Subject.Errors.Should().Contain(e => e.Contains("cardNumber"));
    }

    [Fact]
    public async Task Card_number_is_stored_normalized_so_one_card_cannot_become_two_rows()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(AnId()) with { CardNumber = " #a-1001 " }, "o");

        // The decorative '#', the case and the padding are all conventions, not data.
        result.Should().BeOfType<RegistrationResult.Created>()
            .Subject.Beneficiary.CardNumber.Should().Be("A-1001");
    }

    [Fact]
    public async Task A_card_already_in_issue_is_refused_against_its_normalized_form()
    {
        var holder = Guid.NewGuid();
        var lookup = new FakeLookup();
        lookup.Cards["A-1001"] = holder;
        var reg = new BeneficiaryRegistrar(lookup, TimeProvider.System);

        // Typed without the '#' and in lower case — still the same card.
        var result = await reg.RegisterAsync(Req(AnId()) with { CardNumber = "a-1001" }, "o");

        var dup = result.Should().BeOfType<RegistrationResult.DuplicateCardNumber>().Subject;
        dup.ExistingBeneficiaryId.Should().Be(holder);
    }

    [Fact]
    public async Task Coverage_must_be_elected_at_registration()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(AnId()) with { Enrolment = null }, "o");

        result.Should().BeOfType<RegistrationResult.Invalid>()
            .Subject.Errors.Should().Contain(e => e.Contains("plan"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Contribution_outside_nought_to_a_hundred_is_refused(decimal percent)
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(
            Req(AnId()) with { Enrolment = Intent with { ContributionPercent = percent } }, "o");

        result.Should().BeOfType<RegistrationResult.Invalid>()
            .Subject.Errors.Should().Contain(e => e.Contains("contribution"));
    }

    [Fact]
    public async Task A_birth_date_in_the_future_is_refused()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));

        var result = await reg.RegisterAsync(Req(AnId()) with { BirthDate = tomorrow }, "o");

        result.Should().BeOfType<RegistrationResult.Invalid>()
            .Subject.Errors.Should().Contain(e => e.Contains("birthDate"));
    }

    [Fact]
    public async Task An_approximate_birth_date_is_kept_with_its_flag()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(
            Req(AnId()) with { BirthDate = new DateOnly(1990, 1, 1), BirthDateIsApproximate = true }, "o");

        // The date is still stored — an age-banded rule needs something — but it travels with the flag that
        // stops anything downstream presenting it as exact.
        var created = result.Should().BeOfType<RegistrationResult.Created>().Subject;
        created.Beneficiary.BirthDate.Should().Be(new DateOnly(1990, 1, 1));
        created.Beneficiary.BirthDateIsApproximate.Should().BeTrue();
    }

    [Fact]
    public async Task Age_is_never_stored()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(AnId()), "o");

        // A number written down today is wrong tomorrow. The only lasting fact is the birth date, and every
        // reader derives the age from it — so the entity must not have anywhere to put one.
        var created = result.Should().BeOfType<RegistrationResult.Created>().Subject;
        typeof(Beneficiary).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Age", StringComparison.OrdinalIgnoreCase));
        created.Beneficiary.BirthDate.Should().NotBeNull();
    }

    [Fact]
    public async Task Sex_and_nationality_are_required_and_nationality_is_upper_cased()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var missing = await reg.RegisterAsync(
            Req(AnId()) with { Sex = null, NationalityCode = null }, "o");
        missing.Should().BeOfType<RegistrationResult.Invalid>()
            .Subject.Errors.Should().Contain(e => e.Contains("sex"))
            .And.Contain(e => e.Contains("nationalityCode"));

        var ok = await reg.RegisterAsync(Req(AnId()) with { NationalityCode = "sy" }, "o");
        ok.Should().BeOfType<RegistrationResult.Created>()
            .Subject.Beneficiary.NationalityCode.Should().Be("SY");
    }

    [Fact]
    public async Task Blank_notes_are_dropped_rather_than_stored_empty()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(AnId()) with
        {
            Notes = [new NewRegistrationNote(1, "  Type 2 diabetes  "), new NewRegistrationNote(2, "   ")],
        }, "o");

        // An empty slot and a slot somebody cleared read identically later; storing blanks makes "is the
        // diagnosis on file" unanswerable.
        var created = result.Should().BeOfType<RegistrationResult.Created>().Subject;
        created.Notes.Should().ContainSingle();
        created.Notes[0].Slot.Should().Be((short)1);
        created.Notes[0].Value.Should().Be("Type 2 diabetes");
    }

    [Fact]
    public async Task A_note_slot_cannot_be_filled_twice()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(Req(AnId()) with
        {
            Notes = [new NewRegistrationNote(1, "one"), new NewRegistrationNote(1, "two")],
        }, "o");

        result.Should().BeOfType<RegistrationResult.Invalid>()
            .Subject.Errors.Should().Contain(e => e.Contains("only once"));
    }

    // ── Intake mode ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_intake_row_needs_no_identity_document_and_no_elected_plan()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        // What a bulk row actually carries: a person and a card. The document is absent because the sheet has
        // none, and the coverage is elected by the row's own plan/tier columns straight after this call.
        var result = await reg.RegisterAsync(
            new RegisterBeneficiaryRequest("Amina", "Yusuf", new DateOnly(1990, 1, 1), "Female", "SY", [], [])
            {
                CardNumber = "#A-1001", Enrolment = null, IsIntake = true,
            }, "importer");

        var created = result.Should().BeOfType<RegistrationResult.Created>().Subject;
        created.Beneficiary.CardNumber.Should().Be("A-1001");
        created.Beneficiary.Identifiers.Should().BeEmpty();
        created.Intent.Should().BeNull();
    }

    [Fact]
    public async Task Intake_mode_narrows_exactly_two_checks_and_no_others()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        // The card, the name allowlist, sex and nationality are all still enforced — intake is not a back
        // door into the registry, it is two rules that do not apply to a spreadsheet.
        var result = await reg.RegisterAsync(
            new RegisterBeneficiaryRequest("<script>", "", null, null, null, [], [])
            {
                CardNumber = "", IsIntake = true,
            }, "importer");

        var errors = result.Should().BeOfType<RegistrationResult.Invalid>().Subject.Errors;
        errors.Should().Contain(e => e.Contains("cardNumber"))
            .And.Contain(e => e.Contains("givenName"))
            .And.Contain(e => e.Contains("familyName"))
            .And.Contain(e => e.Contains("sex"))
            .And.Contain(e => e.Contains("nationalityCode"));
        // ...and only those two are waived.
        errors.Should().NotContain(e => e.Contains("identifier"));
        errors.Should().NotContain(e => e.Contains("plan, network tier"));
    }

    [Fact]
    public async Task An_intake_row_is_still_refused_when_the_card_belongs_to_somebody_else()
    {
        var holder = Guid.NewGuid();
        var lookup = new FakeLookup();
        lookup.Cards["A-1001"] = holder;
        var reg = new BeneficiaryRegistrar(lookup, TimeProvider.System);

        var result = await reg.RegisterAsync(
            new RegisterBeneficiaryRequest("Amina", "Yusuf", new DateOnly(1990, 1, 1), "Female", "SY", [], [])
            {
                CardNumber = "A-1001", IsIntake = true,
            }, "importer");

        result.Should().BeOfType<RegistrationResult.DuplicateCardNumber>()
            .Subject.ExistingBeneficiaryId.Should().Be(holder);
    }

    [Fact]
    public async Task A_slot_that_does_not_exist_is_refused()
    {
        var reg = new BeneficiaryRegistrar(new FakeLookup(), TimeProvider.System);

        var result = await reg.RegisterAsync(
            Req(AnId()) with { Notes = [new NewRegistrationNote(7, "x")] }, "o");

        result.Should().BeOfType<RegistrationResult.Invalid>()
            .Subject.Errors.Should().Contain(e => e.Contains("slot 7"));
    }
}

/// <summary>
/// The clinical slots are the reason the note table carries a visibility at all: an administrative role types
/// a diagnosis into slot 1, and being the role that typed it is not a reason to be the role that reads it back.
/// </summary>
public class RegistrationNoteVisibilityTests
{
    [Fact]
    public void Slot_one_and_three_are_clinical_the_rest_administrative()
    {
        RegistrationNoteSlots.VisibilityOf(1).Should().Be(NoteVisibility.Clinical);   // known diagnosis
        RegistrationNoteSlots.VisibilityOf(3).Should().Be(NoteVisibility.Clinical);   // insulin patient
        foreach (short slot in new short[] { 2, 4, 5, 6 })
            RegistrationNoteSlots.VisibilityOf(slot).Should().Be(NoteVisibility.Administrative);
    }

    [Theory]
    [InlineData("beneficiary_mgmt", false)]
    [InlineData("beneficiary_mgmt_supervisor", false)]
    [InlineData("finance", false)]
    [InlineData("reception", false)]
    [InlineData("doctor", true)]
    [InlineData("nurse", true)]
    [InlineData("medical_approval", true)]
    [InlineData("case_manager", true)]
    public void Only_clinical_roles_read_clinical_slots(string role, bool mayRead)
        => Mersal.Patient.Api.NoteProjection.MayReadClinical([role]).Should().Be(mayRead);

    [Fact]
    public void A_withheld_slot_stays_visible_as_a_named_locked_state()
    {
        var registrationId = Guid.NewGuid();
        List<RegistrationNote> notes =
        [
            new() { RegistrationId = registrationId, Slot = 1, Value = "Type 2 diabetes", Visibility = NoteVisibility.Clinical },
            new() { RegistrationId = registrationId, Slot = 2, Value = "EGP 40,000", Visibility = NoteVisibility.Administrative },
        ];

        var projected = Mersal.Patient.Api.NoteProjection.Project(notes, mayReadClinical: false);

        // The row does NOT disappear: "no diagnosis on file" and "a diagnosis you may not read" are different
        // facts, and an officer who cannot tell them apart re-asks the beneficiary for what we already hold.
        projected.Should().HaveCount(2);
        var diagnosis = projected.Single(n => n.Slot == 1);
        diagnosis.Withheld.Should().BeTrue();
        diagnosis.Value.Should().BeNull();
        diagnosis.LabelEn.Should().Be("Known diagnosis");

        projected.Single(n => n.Slot == 2).Value.Should().Be("EGP 40,000");
    }

    [Fact]
    public void A_clinical_role_receives_the_content()
    {
        var notes = new List<RegistrationNote>
        {
            new() { Slot = 1, Value = "Type 2 diabetes", Visibility = NoteVisibility.Clinical },
        };

        var projected = Mersal.Patient.Api.NoteProjection.Project(notes, mayReadClinical: true);

        projected.Single().Withheld.Should().BeFalse();
        projected.Single().Value.Should().Be("Type 2 diabetes");
    }
}

public class LifecycleTests
{
    [Theory]
    [InlineData(BeneficiaryStatus.Pending, BeneficiaryStatus.Active, true)]
    [InlineData(BeneficiaryStatus.Active, BeneficiaryStatus.Suspended, true)]
    [InlineData(BeneficiaryStatus.Suspended, BeneficiaryStatus.Active, true)]
    [InlineData(BeneficiaryStatus.Expired, BeneficiaryStatus.Active, true)]
    [InlineData(BeneficiaryStatus.Pending, BeneficiaryStatus.Suspended, false)] // illegal
    [InlineData(BeneficiaryStatus.Blocked, BeneficiaryStatus.Suspended, false)] // illegal
    public void Transition_legality(BeneficiaryStatus from, BeneficiaryStatus to, bool legal)
        => BeneficiaryLifecycle.CanTransition(from, to).Should().Be(legal);

    [Fact]
    public void Suspend_and_block_require_a_reason()
    {
        BeneficiaryLifecycle.Validate(BeneficiaryStatus.Active, BeneficiaryStatus.Suspended, reason: null)
            .Should().Contain("reason is required");
        BeneficiaryLifecycle.Validate(BeneficiaryStatus.Active, BeneficiaryStatus.Suspended, reason: "fraud review")
            .Should().BeNull();
    }

    [Fact]
    public void Illegal_transition_is_reported()
        => BeneficiaryLifecycle.Validate(BeneficiaryStatus.Pending, BeneficiaryStatus.Blocked, "x")
            .Should().Contain("illegal transition");

    [Fact]
    public void MemberNo_formats_per_year_and_sequence()
        => MemberNo.Format(2026, 42).Should().Be("MRS-M-2026-000042");
}
