namespace Mersal.Patient.Domain;

// Patient domain entities per 15-database-erd §4 + 22-data-dictionary. UUID v7 PKs, standard audit
// columns; _history twins are written by trigger/outbox, never app code (see migration).

public enum BeneficiaryStatus { Pending, Active, Suspended, Expired, Blocked, Inactive }

/// <summary>
/// The identifier kinds a beneficiary can be found by.
/// </summary>
/// <remarks>
/// 26.6 added <see cref="CardNumber"/>. The column existed on <c>beneficiary</c> since phase 1 and was
/// unique among live rows, but no search filter reached it and the enum had no member — so "look the patient
/// up by the number on their card", which is how a pharmacy counter actually works, could not be expressed
/// at all. It is deliberately LAST: a card number is printed on something that gets shared, photographed and
/// reused, so it is a lookup key and never an authenticator (doc 43 §7).
/// </remarks>
public enum IdentifierType { NationalID, Passport, RefugeeID, UNHCRNo, MemberNo, CardNumber }

public enum ContactType { Phone, Email, Address, EmergencyContact }

public enum DependentRelationship { Child, Spouse, Parent, Other }

public sealed class Beneficiary
{
    public Guid BeneficiaryId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011); stamped from principal
    public string? MemberNo { get; set; }                 // MRS-M-* issued at activation (1.4)

    /// <summary>The number printed on the physical card, captured by the officer at the desk.
    ///
    /// <para>Deliberately NOT <see cref="MemberNo"/>. The member number is issued by this service at
    /// activation; the card is already in the beneficiary's hand while their application is still Pending.
    /// One field could not be both without either issuing a member number to an unapproved person or
    /// refusing to record the card they are holding. Unique among non-deleted rows — two people on one card
    /// means the second one's consumption lands on the first one's limits.</para></summary>
    public string? CardNumber { get; set; }

    public string GivenName { get; set; } = default!;
    public string? MiddleName { get; set; }
    public string FamilyName { get; set; } = default!;
    public DateOnly? BirthDate { get; set; }

    /// <summary>True when the birth date was transcribed from an incomplete document. The date is still
    /// stored — an age-banded eligibility rule needs something — but nothing downstream may present it as
    /// exact. A null date could not carry this distinction, which is why registration never had anywhere to
    /// put an estimated date and operators left the field empty instead.</summary>
    public bool BirthDateIsApproximate { get; set; }

    public string? Sex { get; set; }
    public string? NationalityCode { get; set; }

    /// <summary>Programme-side references the desk searches by when no identifier is to hand.</summary>
    public string? IndividualNo { get; set; }
    public string? CaseNo { get; set; }
    public BeneficiaryStatus Status { get; set; } = BeneficiaryStatus.Pending; // created Pending (1.1)
    public Guid? FamilyGroupId { get; set; }

    // Standard audit columns.
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<BeneficiaryIdentifier> Identifiers { get; set; } = [];
    public List<Contact> Contacts { get; set; } = [];
}

public sealed class BeneficiaryIdentifier
{
    public Guid IdentifierId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid BeneficiaryId { get; set; }
    public IdentifierType IdentifierType { get; set; }
    public string IdentifierValue { get; set; } = default!;
    public string? IssuingCountry { get; set; }
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class Contact
{
    public Guid ContactId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid BeneficiaryId { get; set; }
    public ContactType ContactType { get; set; }
    public string Value { get; set; } = default!;
    public string? PreferredChannel { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class FamilyGroup
{
    public Guid FamilyGroupId { get; set; }
    public string TenantId { get; set; } = "";
    public string FamilyCode { get; set; } = default!;
    public Guid? HeadBeneficiaryId { get; set; }
}

public sealed class DependentLink
{
    public Guid DependentLinkId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid FamilyGroupId { get; set; }
    public Guid GuardianBeneficiaryId { get; set; }
    public Guid DependentBeneficiaryId { get; set; }
    public DependentRelationship Relationship { get; set; }
}
