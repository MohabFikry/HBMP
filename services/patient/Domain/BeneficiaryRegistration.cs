using Mersal.Time;

namespace Mersal.Patient.Domain;

/// <summary>Port: check whether an identifier already exists on an active (non-deleted) beneficiary.</summary>
public interface IIdentifierLookup
{
    /// <summary>The beneficiary id owning this active identifier, or null if free.</summary>
    Task<Guid?> FindActiveOwnerAsync(IdentifierType type, string normalizedValue, CancellationToken ct = default);

    /// <summary>The beneficiary holding this card number among non-deleted rows, or null if free. Separate
    /// from the identifier lookup because a card number is not an identity document — it is the number on
    /// the plastic, and it is re-issued to the same person when a card is replaced.</summary>
    Task<Guid?> FindActiveCardHolderAsync(string normalizedCardNumber, CancellationToken ct = default);
}

public sealed record NewIdentifier(IdentifierType Type, string Value, string? IssuingCountry = null, bool IsPrimary = false);
public sealed record NewContact(ContactType Type, string Value, string? PreferredChannel = null, bool IsPrimary = false);

/// <summary>The coverage elected at the desk. Applied by policy-service when the supervisor approves.</summary>
public sealed record NewEnrolmentIntent(
    Guid PlanId,
    Guid NetworkTierId,
    decimal ContributionPercent,
    Guid? DefaultBranchId);

/// <summary>One filled note slot. The VISIBILITY is not accepted from the caller — it is a property of the
/// slot (<see cref="RegistrationNoteSlots"/>), so a client cannot route a diagnosis around the clinical rule
/// by declaring it administrative.</summary>
public sealed record NewRegistrationNote(short Slot, string Value);

public sealed record RegisterBeneficiaryRequest(
    string GivenName,
    string FamilyName,
    DateOnly? BirthDate,
    string? Sex,
    string? NationalityCode,
    IReadOnlyList<NewIdentifier> Identifiers,
    IReadOnlyList<NewContact> Contacts)
{
    public string? CardNumber { get; init; }
    public string? MiddleName { get; init; }
    public bool BirthDateIsApproximate { get; init; }
    public string? IndividualNo { get; init; }
    public string? CaseNo { get; init; }
    public NewEnrolmentIntent? Enrolment { get; init; }
    public IReadOnlyList<NewRegistrationNote> Notes { get; init; } = [];

    /// <summary>
    /// True when this person is arriving through a bulk INTAKE file rather than the registration form.
    ///
    /// <para>Two of the form's requirements do not apply to a file, and pretending otherwise would mean
    /// either refusing every intake row or inventing placeholder data to get past the check:</para>
    /// <list type="bullet">
    ///   <item>an intake sheet carries no identity DOCUMENT — the card number is the reference, and it is
    ///   validated and de-duplicated exactly as an identifier would be;</item>
    ///   <item>coverage is elected by the bulk row itself, from its own plan and tier columns, immediately
    ///   after the person is created — so the request does not carry an intent for the registrar to check.</item>
    /// </list>
    /// <para>Everything else — the name allowlist, the card format, sex, nationality, the future-birthdate
    /// rule, the note slots — is enforced identically. This narrows two checks; it does not open a back door
    /// around the rest.</para>
    /// </summary>
    public bool IsIntake { get; init; }
}

public abstract record RegistrationResult
{
    /// <summary>Created in status Pending. <paramref name="Intent"/> and <paramref name="Notes"/> are the
    /// registration-scoped rows the caller persists alongside the person, in the same transaction — they
    /// hang off the registration id, which does not exist until the Api creates it.</summary>
    public sealed record Created(
        Beneficiary Beneficiary,
        NewEnrolmentIntent? Intent,
        IReadOnlyList<NewRegistrationNote> Notes) : RegistrationResult;
    /// <summary>An active identifier already exists — caller should open/merge, not duplicate (US-001, 409).</summary>
    public sealed record DuplicateIdentifier(Guid ExistingBeneficiaryId, IdentifierType Type, string Value) : RegistrationResult;
    /// <summary>The card number is already held by somebody else — a distinct 409 from the identifier one,
    /// because the remedy differs: an identifier clash means "this is the same person, open them"; a card
    /// clash usually means the card was mis-read or has been re-issued without the old one being retired.</summary>
    public sealed record DuplicateCardNumber(Guid ExistingBeneficiaryId, string CardNumber) : RegistrationResult;
    /// <summary>Validation failure (missing fields / bad identifier format) — 400.</summary>
    public sealed record Invalid(IReadOnlyList<string> Errors) : RegistrationResult;
}

/// <summary>
/// Registration business rules (US-001), independent of persistence/transport so they are unit-tested:
/// require name + ≥1 valid identifier, validate each identifier format, and block duplicates against
/// active identifiers (return the existing id, never write a second row). New beneficiaries are Pending.
/// </summary>
public sealed class BeneficiaryRegistrar(IIdentifierLookup lookup, TimeProvider clock)
{
    public async Task<RegistrationResult> RegisterAsync(RegisterBeneficiaryRequest req, string? actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(req.GivenName)) errors.Add("givenName is required");
        else if (!PersonFieldValidation.IsValidName(req.GivenName)) errors.Add("givenName contains characters that do not appear in names");
        if (string.IsNullOrWhiteSpace(req.FamilyName)) errors.Add("familyName is required");
        else if (!PersonFieldValidation.IsValidName(req.FamilyName)) errors.Add("familyName contains characters that do not appear in names");
        // Optional, but held to the same allowlist as the other two when present — a middle name reaches the
        // same PDFs, SMS templates and CSV exports.
        if (!string.IsNullOrWhiteSpace(req.MiddleName) && !PersonFieldValidation.IsValidName(req.MiddleName))
            errors.Add("middleName contains characters that do not appear in names");
        // An intake file's reference IS the card number, which is required and de-duplicated below.
        if (!req.IsIntake && (req.Identifiers is null || req.Identifiers.Count == 0))
            errors.Add("at least one identifier is required");

        if (string.IsNullOrWhiteSpace(req.CardNumber)) errors.Add("cardNumber is required");
        else if (!PersonFieldValidation.IsValidCardNumber(req.CardNumber))
            errors.Add($"'{req.CardNumber}' is not a valid card number");

        if (!PersonFieldValidation.IsValidSex(req.Sex)) errors.Add("sex is required (Male, Female, Other or Unknown)");
        if (!PersonFieldValidation.IsValidNationalityCode(req.NationalityCode))
            errors.Add("nationalityCode is required as an ISO 3166-1 alpha-2 code");
        if (!PersonFieldValidation.IsValidReference(req.IndividualNo)) errors.Add("individualNo contains unsupported characters");
        if (!PersonFieldValidation.IsValidReference(req.CaseNo)) errors.Add("caseNo contains unsupported characters");

        // A birth date in the future is not a typo the desk can be left to notice: it silently inverts every
        // age-banded eligibility rule that reads it.
        if (req.BirthDate is { } dob && dob > BusinessCalendar.DateIn(clock.GetUtcNow()))
            errors.Add("birthDate cannot be in the future");

        if (req.Enrolment is { } intent)
        {
            if (intent.PlanId == Guid.Empty) errors.Add("a plan is required");
            if (intent.NetworkTierId == Guid.Empty) errors.Add("a network tier is required");
            if (intent.ContributionPercent is < 0 or > 100)
                errors.Add("contribution must be a percentage between 0 and 100");
        }
        // A bulk row elects its own coverage straight after this call, from its plan and tier columns.
        else if (!req.IsIntake) errors.Add("plan, network tier and contribution are required");

        foreach (var note in req.Notes ?? [])
        {
            if (RegistrationNoteSlots.For(note.Slot) is null) errors.Add($"note slot {note.Slot} does not exist");
            else if (note.Value?.Length > 2000) errors.Add($"note {note.Slot} is too long");
        }
        if ((req.Notes ?? []).Select(n => n.Slot).Distinct().Count() != (req.Notes ?? []).Count)
            errors.Add("a note slot may be filled only once");

        // Contacts were stored UNVALIDATED (QA P0-2: a phone of "abcdefg" persisted). A junk phone is worse
        // than no phone — every later workflow (eligibility SMS, call-centre callback) trusts this value.
        foreach (var c in req.Contacts ?? [])
        {
            if (!PersonFieldValidation.IsValidContact(c.Type, c.Value))
                errors.Add($"'{c.Value}' is not a valid {c.Type}");
        }

        foreach (var id in req.Identifiers ?? [])
        {
            // Validate the normalized value we actually store, so trivial variants (case/spacing) are
            // treated consistently for both validation and dedup.
            if (!IdentifierValidation.IsValid(id.Type, IdentifierValidation.Normalize(id.Value), out var err)) errors.Add(err!);
        }
        if (errors.Count > 0) return new RegistrationResult.Invalid(errors);

        // Duplicate detection against ACTIVE identifiers — return the existing record, do not duplicate.
        foreach (var id in req.Identifiers ?? [])
        {
            var norm = IdentifierValidation.Normalize(id.Value);
            var owner = await lookup.FindActiveOwnerAsync(id.Type, norm, ct);
            if (owner is not null) return new RegistrationResult.DuplicateIdentifier(owner.Value, id.Type, id.Value);
        }

        // The same check for the card, against the normalized form so "#A-1234" and "a 1234" cannot become
        // two rows for one card. Checked here as well as by the partial unique index: the index is what makes
        // it true under concurrency, this is what makes the answer a useful message instead of a 500.
        var card = PersonFieldValidation.NormalizeCardNumber(req.CardNumber);
        if (await lookup.FindActiveCardHolderAsync(card, ct) is { } cardHolder)
            return new RegistrationResult.DuplicateCardNumber(cardHolder, card);

        var now = clock.GetUtcNow();
        var beneficiary = new Beneficiary
        {
            BeneficiaryId = Guid.NewGuid(),
            CardNumber = card,
            GivenName = req.GivenName.Trim(),
            MiddleName = string.IsNullOrWhiteSpace(req.MiddleName) ? null : req.MiddleName.Trim(),
            FamilyName = req.FamilyName.Trim(),
            BirthDate = req.BirthDate,
            BirthDateIsApproximate = req.BirthDateIsApproximate,
            Sex = req.Sex,
            NationalityCode = req.NationalityCode?.Trim().ToUpperInvariant(),
            IndividualNo = string.IsNullOrWhiteSpace(req.IndividualNo) ? null : req.IndividualNo.Trim(),
            CaseNo = string.IsNullOrWhiteSpace(req.CaseNo) ? null : req.CaseNo.Trim(),
            Status = BeneficiaryStatus.Pending,   // created Pending (activation is 1.4)
            CreatedBy = actor, UpdatedBy = actor, CreatedAt = now, UpdatedAt = now,
            Identifiers = (req.Identifiers ?? []).Select(i => new BeneficiaryIdentifier
            {
                IdentifierId = Guid.NewGuid(),
                IdentifierType = i.Type,
                IdentifierValue = IdentifierValidation.Normalize(i.Value),
                IssuingCountry = i.IssuingCountry,
                IsPrimary = i.IsPrimary,
            }).ToList(),
            Contacts = (req.Contacts ?? []).Select(c => new Contact
            {
                ContactId = Guid.NewGuid(),
                ContactType = c.Type, Value = c.Value.Trim(),
                PreferredChannel = c.PreferredChannel, IsPrimary = c.IsPrimary,
            }).ToList(),
        };
        // Empty notes are dropped rather than stored blank: an empty slot and a slot somebody deliberately
        // cleared read identically later, and a row of empty strings makes "is the diagnosis on file" a
        // question no query can answer.
        var notes = (req.Notes ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n.Value))
            .Select(n => n with { Value = n.Value.Trim() })
            .ToList();
        return new RegistrationResult.Created(beneficiary, req.Enrolment, notes);
    }
}
