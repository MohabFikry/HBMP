namespace Mersal.Patient.Domain;

// Patient domain entities per 15-database-erd §4 + 22-data-dictionary. UUID v7 PKs, standard audit
// columns; _history twins are written by trigger/outbox, never app code (see migration).

public enum BeneficiaryStatus { Pending, Active, Suspended, Expired, Blocked, Inactive }

public enum IdentifierType { NationalID, Passport, RefugeeID, UNHCRNo, MemberNo }

public enum ContactType { Phone, Email, Address, EmergencyContact }

public enum DependentRelationship { Child, Spouse, Parent, Other }

public sealed class Beneficiary
{
    public Guid BeneficiaryId { get; set; }
    public string TenantId { get; set; } = "";            // RLS tenant scope (ADR-0011); stamped from principal
    public string? MemberNo { get; set; }                 // MRS-M-* issued at activation (1.4)
    public string GivenName { get; set; } = default!;
    public string FamilyName { get; set; } = default!;
    public DateOnly? BirthDate { get; set; }
    public string? Sex { get; set; }
    public string? NationalityCode { get; set; }
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
