namespace Mersal.Patient.Domain;

/// <summary>
/// The registration APPLICATION sub-state (distinct from beneficiary.status). The beneficiary stays
/// Pending until activation, then becomes Active (1.4, US-003). Wizard steps set the guard flags.
/// </summary>
public enum RegistrationStatus { Pending, InfoRequested, Rejected, Active }

public enum RegistrationDecision { Approve, RequestInfo, Reject }

public sealed class Registration
{
    public Guid RegistrationId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid BeneficiaryId { get; set; }
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Pending;

    /// <summary>Approval guards (US-003): documents verified AND a policy/coverage bound.</summary>
    public bool DocumentsVerified { get; set; }
    public bool CoverageBound { get; set; }

    /// <summary>The CURRENT outstanding note — what is missing, or why it was refused. The history of how it
    /// got here, and the officer's answer, live on <see cref="RegistrationThreadEntry"/>.</summary>
    public string? Notes { get; set; }

    /// <summary>The officer who filed the application, and their name as it stood when they filed it.
    ///
    /// <para>The name is a copy on purpose. Resolving it through identity-service on every worklist read would
    /// make the queue unable to render a column while that service restarts, and someone who has since left
    /// must still be named on the applications they filed: the record is of what happened.</para>
    ///
    /// <para>This is also the address a RequestInfo decision is delivered to — without it, "we need more
    /// information" has no queue to land in.</para></summary>
    public string? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }

    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Whether a thread entry is a ruling or an answer to one. Rendered differently, and never
/// interchangeable — a reply that reads as a decision is a reply that gets acted on as one.</summary>
public enum RegistrationThreadKind { Decision, Reply }

/// <summary>
/// One entry in the conversation about a registration.
///
/// <para><b>Append-only.</b> The application role holds SELECT and INSERT on this table and nothing else
/// (migration 0005), so an entry cannot be edited or removed after the fact. A supervisor's stated reason for
/// refusing an application is evidence, and evidence that can be quietly rewritten is not evidence.</para>
///
/// <para>This is what makes RequestInfo a workflow rather than a dead end: the decision writes a Decision
/// entry, the officer it was addressed to answers with a Reply, and both are on the record in order.</para>
/// </summary>
public sealed class RegistrationThreadEntry
{
    public Guid EntryId { get; set; }
    public Guid RegistrationId { get; set; }
    public string TenantId { get; set; } = "";
    public RegistrationThreadKind Kind { get; set; }

    /// <summary>Which decision this entry recorded, or null on a reply.</summary>
    public RegistrationDecision? Decision { get; set; }

    public string Body { get; set; } = default!;
    public string? AuthorUserId { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorRole { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The coverage the officer is registering this person ONTO — captured at the desk, applied at approval.
///
/// <para>An INTENT rather than a membership, because policy-service owns enrollments and the supervisor's
/// approval is what creates one. That is precisely what <see cref="Registration.CoverageBound"/> has always
/// meant; until now it was a checkbox somebody ticked, with nothing behind it. Writing an enrollment at the
/// desk instead would grant coverage before anyone approved the application, and would need a cross-service
/// compensation to undo when the application is rejected.</para>
/// </summary>
public sealed class EnrolmentIntent
{
    public Guid RegistrationId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PlanId { get; set; }
    public Guid NetworkTierId { get; set; }

    /// <summary>The member's share of the service price, 0..100. The one value that varies member by member
    /// inside an otherwise shared batch.</summary>
    public decimal ContributionPercent { get; set; }

    /// <summary>The internal clinic this beneficiary is normally seen at. Optional — not everyone is tied to
    /// one, and care happens wherever the member turns up regardless.</summary>
    public Guid? DefaultBranchId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>How far a note slot's content may travel. Mirrors the note/document visibility vocabulary that
/// policy-service already enforces, so one rule governs a typed note and a scanned report alike.</summary>
public enum NoteVisibility { Administrative, Clinical }

/// <summary>
/// One of the six standing note slots on a registration.
///
/// <para>Fixed slots rather than free-form rows: slot 1 is ALWAYS the known diagnosis and slot 3 always the
/// insulin flag, so a report can read a slot without parsing prose and the labels stay identical across the
/// form, the export and the profile.</para>
/// </summary>
public sealed class RegistrationNote
{
    public Guid RegistrationId { get; set; }
    public string TenantId { get; set; } = "";
    public short Slot { get; set; }
    public string Value { get; set; } = default!;
    public NoteVisibility Visibility { get; set; } = NoteVisibility.Administrative;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// What each of the six slots means, and how far its content may travel.
///
/// <para><b>Slots 1 and 3 are clinical facts on a form owned by an administrative role.</b> A known diagnosis
/// and "insulin patient" are exactly the material 18-security-model.md keeps away from finance, claims and
/// reception — and beneficiary management sits outside the clinical allow-list for every other carrier of the
/// same facts. Classifying them here means the projection that already withholds a scanned lab result
/// withholds these too, while the desk can still FILE them: capture is not disclosure.</para>
/// </summary>
public static class RegistrationNoteSlots
{
    public sealed record Slot(short Number, string LabelEn, string LabelAr, NoteVisibility Visibility);

    public static readonly IReadOnlyList<Slot> All =
    [
        new(1, "Known diagnosis", "التشخيص المعروف", NoteVisibility.Clinical),
        new(2, "Forecasted case cost", "التكلفة المتوقعة للحالة", NoteVisibility.Administrative),
        new(3, "Insulin patient", "مريض أنسولين", NoteVisibility.Clinical),
        new(4, "Most visited speciality", "التخصص الأكثر زيارة", NoteVisibility.Administrative),
        new(5, "Note 5", "ملاحظة ٥", NoteVisibility.Administrative),
        new(6, "Note 6", "ملاحظة ٦", NoteVisibility.Administrative),
    ];

    public static Slot? For(short number) => All.FirstOrDefault(s => s.Number == number);

    /// <summary>The visibility a slot's content carries, regardless of what a caller claimed. A client that
    /// asked for a diagnosis to be stored Administrative is asking to route it around the clinical rule.</summary>
    public static NoteVisibility VisibilityOf(short number) =>
        For(number)?.Visibility ?? NoteVisibility.Administrative;
}

/// <summary>Pure decision rules for the registration workflow (unit-tested; Api orchestrates persistence).</summary>
public static class RegistrationRules
{
    /// <summary>
    /// Validate a decision against the current state + guards. Returns an error message, or null if the
    /// decision is allowed. Approve needs docs verified + coverage bound; Reject/RequestInfo need notes.
    /// </summary>
    public static string? ValidateDecision(Registration reg, RegistrationDecision decision, string? notes)
    {
        ArgumentNullException.ThrowIfNull(reg);
        if (reg.Status is RegistrationStatus.Active or RegistrationStatus.Rejected)
            return $"registration is already {reg.Status}";

        return decision switch
        {
            RegistrationDecision.Reject when string.IsNullOrWhiteSpace(notes) => "a reason is required to reject",
            RegistrationDecision.RequestInfo when string.IsNullOrWhiteSpace(notes) => "notes describing the missing information are required",
            RegistrationDecision.Approve when !reg.DocumentsVerified => "cannot approve: documents are not verified",
            RegistrationDecision.Approve when !reg.CoverageBound => "cannot approve: no policy/coverage is bound",
            _ => null,
        };
    }

    /// <summary>The registration status resulting from an (already-validated) decision.</summary>
    public static RegistrationStatus ResultOf(RegistrationDecision decision) => decision switch
    {
        RegistrationDecision.Approve => RegistrationStatus.Active,
        RegistrationDecision.RequestInfo => RegistrationStatus.InfoRequested,
        RegistrationDecision.Reject => RegistrationStatus.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };
}
