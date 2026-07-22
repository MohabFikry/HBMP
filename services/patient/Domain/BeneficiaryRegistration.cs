namespace Mersal.Patient.Domain;

/// <summary>Port: check whether an identifier already exists on an active (non-deleted) beneficiary.</summary>
public interface IIdentifierLookup
{
    /// <summary>The beneficiary id owning this active identifier, or null if free.</summary>
    Task<Guid?> FindActiveOwnerAsync(IdentifierType type, string normalizedValue, CancellationToken ct = default);
}

public sealed record NewIdentifier(IdentifierType Type, string Value, string? IssuingCountry = null, bool IsPrimary = false);
public sealed record NewContact(ContactType Type, string Value, string? PreferredChannel = null, bool IsPrimary = false);

public sealed record RegisterBeneficiaryRequest(
    string GivenName,
    string FamilyName,
    DateOnly? BirthDate,
    string? Sex,
    string? NationalityCode,
    IReadOnlyList<NewIdentifier> Identifiers,
    IReadOnlyList<NewContact> Contacts);

public abstract record RegistrationResult
{
    /// <summary>Created in status Pending.</summary>
    public sealed record Created(Beneficiary Beneficiary) : RegistrationResult;
    /// <summary>An active identifier already exists — caller should open/merge, not duplicate (US-001, 409).</summary>
    public sealed record DuplicateIdentifier(Guid ExistingBeneficiaryId, IdentifierType Type, string Value) : RegistrationResult;
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
        if (string.IsNullOrWhiteSpace(req.FamilyName)) errors.Add("familyName is required");
        if (req.Identifiers is null || req.Identifiers.Count == 0) errors.Add("at least one identifier is required");

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

        var now = clock.GetUtcNow();
        var beneficiary = new Beneficiary
        {
            BeneficiaryId = Guid.NewGuid(),
            GivenName = req.GivenName.Trim(),
            FamilyName = req.FamilyName.Trim(),
            BirthDate = req.BirthDate,
            Sex = req.Sex,
            NationalityCode = req.NationalityCode,
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
        return new RegistrationResult.Created(beneficiary);
    }
}
