using Mersal.Patient.Domain;

namespace Mersal.Patient.Api;

/// <summary>Min-necessary beneficiary projection (registration/approval roles ≠ EMR).</summary>
public sealed record BeneficiaryDto(
    Guid BeneficiaryId, string? MemberNo, string GivenName, string FamilyName,
    string Status, IReadOnlyList<IdentifierDto> Identifiers)
{
    public static BeneficiaryDto From(Beneficiary b) => new(
        b.BeneficiaryId, b.MemberNo, b.GivenName, b.FamilyName, b.Status.ToString(),
        b.Identifiers.Where(i => !i.IsDeleted)
            .Select(i => new IdentifierDto(i.IdentifierType.ToString(), i.IdentifierValue, i.IsPrimary)).ToList());
}

public sealed record IdentifierDto(string Type, string Value, bool IsPrimary);
