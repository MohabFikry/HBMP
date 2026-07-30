using Mersal.Patient.Domain;

namespace Mersal.Patient.Api;

/// <summary>Min-necessary beneficiary projection (registration/approval roles ≠ EMR).</summary>
public sealed record BeneficiaryDto(
    Guid BeneficiaryId, string? MemberNo, string? CardNumber,
    string GivenName, string? MiddleName, string FamilyName,
    string Status, IReadOnlyList<IdentifierDto> Identifiers)
{
    public static BeneficiaryDto From(Beneficiary b) => new(
        b.BeneficiaryId, b.MemberNo, b.CardNumber, b.GivenName, b.MiddleName, b.FamilyName, b.Status.ToString(),
        b.Identifiers.Where(i => !i.IsDeleted)
            .Select(i => new IdentifierDto(i.IdentifierType.ToString(), i.IdentifierValue, i.IsPrimary)).ToList());
}

public sealed record IdentifierDto(string Type, string Value, bool IsPrimary);

public sealed record CreateRegistration(Guid BeneficiaryId);
public sealed record PatchRegistration(bool? DocumentsVerified, bool? CoverageBound, string? Notes);
public sealed record DecisionRequest(string Decision, string? Notes);
public sealed record StatusChange(string ToStatus, string? Reason);

/// <summary>The coverage elected at the desk, as it arrives over the wire.</summary>
public sealed record EnrolmentIntentDto(
    Guid PlanId, Guid NetworkTierId, decimal ContributionPercent, Guid? DefaultBranchId);

/// <summary>A filled note slot. No visibility field: the slot decides it (see
/// <see cref="RegistrationNoteSlots"/>), so a caller cannot declare a diagnosis administrative.</summary>
public sealed record RegistrationNoteDto(short Slot, string Value);

/// <summary>
/// The registration request as the SPA sends it. Deliberately a flat, form-shaped record rather than the
/// domain's <see cref="RegisterBeneficiaryRequest"/>: the form has one phone and one identifier, and asking
/// the client to post the domain's collection shape only invites it to post two of either.
/// </summary>
public sealed record RegisterRequest(
    string CardNumber,
    string GivenName,
    string? MiddleName,
    string FamilyName,
    DateOnly? BirthDate,
    bool BirthDateIsApproximate,
    string Sex,
    string NationalityCode,
    string IdentifierType,
    string IdentifierValue,
    string Phone,
    string? IndividualNo,
    string? CaseNo,
    EnrolmentIntentDto? Enrolment,
    IReadOnlyList<RegistrationNoteDto>? Notes)
{
    /// <summary>Map onto the domain request. An unparseable identifier type becomes a domain validation
    /// error rather than an exception here — the registrar already reports bad identifiers per field, and a
    /// 500 for a typo in an enum would be the one failure the operator cannot act on.</summary>
    public RegisterBeneficiaryRequest ToDomain() => new(
        GivenName,
        FamilyName,
        BirthDate,
        Sex,
        NationalityCode,
        [new NewIdentifier(
            Enum.TryParse<IdentifierType>(IdentifierType, ignoreCase: true, out var t) ? t : Domain.IdentifierType.NationalID,
            IdentifierValue ?? "", IsPrimary: true)],
        string.IsNullOrWhiteSpace(Phone) ? [] : [new NewContact(ContactType.Phone, Phone, IsPrimary: true)])
    {
        CardNumber = CardNumber,
        MiddleName = MiddleName,
        BirthDateIsApproximate = BirthDateIsApproximate,
        IndividualNo = IndividualNo,
        CaseNo = CaseNo,
        Enrolment = Enrolment is null
            ? null
            : new NewEnrolmentIntent(Enrolment.PlanId, Enrolment.NetworkTierId,
                Enrolment.ContributionPercent, Enrolment.DefaultBranchId),
        Notes = (Notes ?? []).Select(n => new NewRegistrationNote(n.Slot, n.Value)).ToList(),
    };
}
