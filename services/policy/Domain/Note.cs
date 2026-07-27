namespace Mersal.Policy.Domain;

// Phase 19.3 — notes on policy and member (design 38 §5). Signed, timestamped, append-only, cancellable.

public enum NoteScope { Policy, Member }

/// <summary>What KIND of note this is — how it is filed and filtered. Deliberately separate from
/// <see cref="NoteVisibility"/>: an Exception note can carry clinical reasoning, and a Clinical note can be
/// administrative in content. Conflating them would let the filing category decide who may read the body.</summary>
public enum NoteType
{
    General, Eligibility, Exception, Approval, Complaint, Financial, Clinical, Administrative,
}

/// <summary>What the body is ABOUT, which decides who may read it. Ordered from least to most restricted —
/// the order is load-bearing: visibility may be raised but never lowered.</summary>
public enum NoteVisibility { Administrative, Financial, Clinical, Restricted }

public enum NoteStatus { Active, Cancelled }

/// <summary>
/// A note. Its body is written once and never again.
///
/// <para>The signature fields are SNAPSHOTS rather than a join to identity, because a note written in 2026
/// must still show who wrote it after that person is renamed, changes team, or is de-provisioned. A join would
/// quietly rewrite the signature — or lose it — on exactly the record most likely to be read back in a dispute.</para>
/// </summary>
public sealed class Note
{
    public Guid NoteId { get; set; }
    public string TenantId { get; set; } = "";
    public NoteScope Scope { get; set; }
    /// <summary>policy_id or enrollment_id — a value, so a third scope needs no schema change.</summary>
    public Guid ScopeRef { get; set; }
    public NoteType NoteType { get; set; }

    /// <summary>Written once. Never updated, never deleted (trigger-enforced in 0009).</summary>
    public string Body { get; set; } = default!;

    public NoteVisibility VisibilityClass { get; set; }

    public Guid AuthoredByUserId { get; set; }
    public string AuthoredByUsername { get; set; } = default!;
    public string AuthoredByDisplay { get; set; } = default!;
    public DateTimeOffset AuthoredAt { get; set; }

    public NoteStatus Status { get; set; } = NoteStatus.Active;
    public Guid? CancelledByUserId { get; set; }
    public string? CancelledByUsername { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    public Guid? SupersedesNoteId { get; set; }
    public bool Pinned { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Only the AUTHOR or a supervisor may cancel (design 38 §5.5). A colleague withdrawing someone
    /// else's signed statement without supervisory authority is the case this exists to refuse.</summary>
    public bool MayBeCancelledBy(Guid? userId, bool hasSupervisorScope) =>
        Status == NoteStatus.Active && (hasSupervisorScope || (userId is { } id && id == AuthoredByUserId));

    /// <summary>Reading this note is itself auditable — clinical and restricted material is PHI, and who looked
    /// at it is part of the record (19-audit-strategy).</summary>
    public bool ReadIsAuditable => VisibilityClass is NoteVisibility.Clinical or NoteVisibility.Restricted;
}

/// <summary>
/// Phase 19.3 — WHO may read a note's body, by visibility class.
///
/// <para>This is the minimum-necessary control, and it is a service-side projection rather than a UI concern:
/// a Finance or Call-Centre principal must never receive a clinical body IN THE PAYLOAD, not merely on a
/// screen that chooses not to render it. The tests assert over the serialized response for that reason.</para>
///
/// <para>What a denied caller DOES receive is the note's existence — type, date, author, status. That is
/// deliberate: "there is a clinical note here, written by Dr X on the 3rd" is what lets an officer know to ask
/// someone who may read it, whereas hiding the note entirely makes the record look empty and sends them away
/// believing nothing was recorded.</para>
/// </summary>
public static class NoteVisibilityRules
{
    /// <summary>Roles entitled to a body of each class. Restricted is absent BY DESIGN — it is never granted by
    /// role, only through the design-37 §6 request/grant flow, which the caller supplies as
    /// <c>hasSensitiveGrant</c>.</summary>
    private static readonly Dictionary<NoteVisibility, string[]> BodyReaders = new()
    {
        // Administrative content is the operational record: everyone who works the member's case reads it.
        [NoteVisibility.Administrative] =
        [
            "beneficiary_mgmt", "beneficiary_mgmt_supervisor", "policy_admin", "org_admin", "super_admin",
            "finance", "claims_officer", "call_center", "medical_approval", "doctor", "nurse", "reception",
            "case_manager", "medical_director",
        ],
        // Financial content: the money roles and administration, not the clinical floor.
        [NoteVisibility.Financial] =
        [
            "finance", "claims_officer", "beneficiary_mgmt", "beneficiary_mgmt_supervisor",
            "policy_admin", "org_admin", "super_admin", "medical_director",
        ],
        // Clinical content: clinicians and the approval team who are entitled to EMR — NOT finance, NOT the
        // call centre, NOT reception. This is the hard rule design 38 §5.6 states and 11-permission-matrix
        // repeats: finance never receives a diagnosis, and a note is not an exception to that.
        [NoteVisibility.Clinical] =
        [
            "doctor", "nurse", "medical_approval", "medical_director", "case_manager", "super_admin",
        ],
    };

    /// <summary>Whether this caller may read the BODY. Existence metadata is separate and much wider.</summary>
    /// <param name="hasSensitiveGrant">The design-37 §6 fact the caller's data owner computed: the reader is
    /// the authoring clinician, or holds an active release grant. The ONLY route to a Restricted body.</param>
    public static bool MayReadBody(
        NoteVisibility visibility, IReadOnlyCollection<string> roles, Guid? userId, Guid authorId,
        bool hasSensitiveGrant = false)
    {
        ArgumentNullException.ThrowIfNull(roles);

        // The author always reads back what they themselves wrote. Withholding someone's own signed statement
        // makes the note surface unusable for the person most likely to need it.
        if (userId is { } id && id == authorId) return true;

        if (visibility == NoteVisibility.Restricted) return hasSensitiveGrant;

        return BodyReaders.TryGetValue(visibility, out var readers)
               && roles.Any(r => readers.Contains(r, StringComparer.Ordinal));
    }
}
