namespace Mersal.Emr.Domain;

/// <summary>
/// 25.4 (design 42 §4) — a dated, reasoned deviation from the weekly recurring pattern.
///
/// Three kinds SUBTRACT availability and one ADDS it. AdHocClinic is modelled here rather than as a second
/// recurring rule because it is the same kind of object — a dated, reasoned, audited departure from the
/// pattern — and putting it elsewhere would mean two places to look when asking "why are there slots that
/// day".
/// </summary>
public enum RosterExceptionKind
{
    /// <summary>This clinician is away.</summary>
    Leave,
    /// <summary>A public holiday: the clinic is shut.</summary>
    PublicHoliday,
    /// <summary>The clinic is shut for another reason (a burst pipe, a power cut, a training day).</summary>
    ClinicClosed,
    /// <summary>An EXTRA clinic on a date the weekly pattern does not cover. The only additive kind.</summary>
    AdHocClinic,
}

/// <remarks>
/// CA1711 ("do not end a type name in 'Exception'") is suppressed here rather than obeyed. The rule exists to
/// stop a reader mistaking a type for <see cref="System.Exception"/>; this is a domain entity in a Domain
/// folder beside <c>Appointment</c>, and it does not derive from anything.
///
/// The alternative is renaming ONLY the C# type, and that costs more than it saves: design 42 §4, the table
/// (<c>emr.roster_exception</c>), the API resource (<c>/api/v1/roster-exceptions</c>), the history twin and
/// the coordinator's screen all say "roster exception". One concept with two names, differing only inside one
/// language, is the kind of divergence that makes people grep twice and find half of it.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Domain term from design 42 §4; matches the table, the route and the UI. Not an exception type.")]
public sealed class RosterException
{
    public Guid ExceptionId { get; set; }
    public string TenantId { get; set; } = default!;

    /// <summary>At least one of <see cref="BranchId"/> / <see cref="PractitionerId"/> is set (DB CHECK).
    /// Branch only ⇒ the whole clinic. Practitioner only ⇒ that clinician wherever they were due to work.
    /// Both ⇒ that clinician at that clinic only — the case of covering another branch that day.</summary>
    public Guid? BranchId { get; set; }
    public Guid? PractitionerId { get; set; }

    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }

    public RosterExceptionKind Kind { get; set; }

    /// <summary>Null start AND end ⇒ whole day. Never one without the other (DB CHECK).</summary>
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    /// <summary>Mandatory. A cancelled clinic day is something a patient will ask about, and "no reason
    /// recorded" is not an answer anyone can give them.</summary>
    public string Reason { get; set; } = default!;

    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>True when this exception REMOVES availability (as opposed to adding it).</summary>
    public bool IsSubtractive => Kind != RosterExceptionKind.AdHocClinic;

    /// <summary>Whole-day exceptions carry no time window and remove (or add) the entire day.</summary>
    public bool IsWholeDay => StartTime is null || EndTime is null;

    /// <summary>Does this exception apply to <paramref name="date"/> for this branch/practitioner pair?
    ///
    /// A null target on the exception means "any" — a ClinicClosed with no practitioner applies to every
    /// clinician at that branch, and a Leave with no branch applies wherever that clinician was due to work.
    /// A null target on the SLOT side (availability with no branch, from before 14.4) must NOT match a
    /// branch-targeted exception: closing Maadi cannot be allowed to silently close a rule whose branch
    /// nobody ever set.</summary>
    public bool AppliesTo(DateOnly date, Guid? branchId, Guid? practitionerId)
    {
        if (IsDeleted) return false;
        if (date < DateFrom || date > DateTo) return false;
        if (BranchId is { } b && branchId != b) return false;
        if (PractitionerId is { } p && practitionerId != p) return false;
        return true;
    }

    /// <summary>Does this exception cover the window [<paramref name="start"/>, <paramref name="end"/>) on a
    /// day it applies to? Whole-day exceptions cover everything; a part-day one covers a slot when the two
    /// OVERLAP at all — a slot half inside a leave window is not half-bookable.</summary>
    public bool Covers(TimeOnly start, TimeOnly end) =>
        IsWholeDay || (start < EndTime!.Value && end > StartTime!.Value);
}
