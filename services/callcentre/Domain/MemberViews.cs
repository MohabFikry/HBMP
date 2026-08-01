namespace Mersal.CallCentre.Domain;

// The call-centre member views (phase 15.2). MINIMUM-NECESSARY + CLINICAL-FREE by construction (design 37 §6,
// 11-permission-matrix): the shapes below CANNOT carry a diagnosis, result, prescription, note, vital, or
// examination detail — there is no field for them. The Call Centre is MemberScoped, so appointments span ALL
// branches.
//
// The proof is an ALLOW-LIST over every string field in this graph (MemberProjectionTests), not a scan for
// clinical-sounding names: a free-text field carries whatever the sibling service wrote into it, which is not
// knowable from here, so a new one fails the test until someone says what it holds. Adding a string below
// means adding it there too — deliberately, because that is the review this file's guarantee depends on.

/// <summary>A member search hit — deliberately thin. ONLY the beneficiary id, a display name, and the member
/// number. No coverage, no contacts, no appointments, no history: this is how the agent picks the right person
/// out of a list, not a disclosure of their file.
///
/// <para><see cref="MemberNo"/> is the real number, unmasked. It used to arrive as <c>•••001</c> because MemberNo
/// was a type the agent could challenge on, and printing it in full would have let them tick that box by reading
/// their own screen. Identity is now confirmed on the phone, so there is no box to tick — and a masked number is
/// no help at all when the caller reads theirs out and the agent has to find them among four people with the same
/// name.</para></summary>
public sealed record MemberMatch(
    Guid BeneficiaryId,
    string DisplayName,
    string? MemberNo);

/// <summary>The member search response.</summary>
public sealed record MemberSearchResult(string Query, int MatchCount, IReadOnlyList<MemberMatch> Matches);

/// <summary>Non-color status semantics for the UI (21-accessibility): hue is never the only signal.</summary>
public sealed record StatusCue(string Label, string Icon, string Shape, string Tone)
{
    public static StatusCue For(string status) => status switch
    {
        "Active" => new("Active", "check-circle", "circle", "positive"),
        "Suspended" => new("Suspended", "pause", "square", "caution"),
        "Expired" => new("Expired", "clock-x", "diamond", "caution"),
        "Blocked" => new("Blocked", "ban", "octagon", "critical"),
        "Inactive" => new("Inactive", "minus-circle", "square", "neutral"),
        _ => new("Pending", "hourglass", "triangle", "neutral"),
    };
}

/// <summary>Identity header (no birth DATE — only an age band, min-necessary).</summary>
public sealed record MemberIdentity(Guid BeneficiaryId, string? MemberNo, string DisplayName, string? AgeBand,
    string Status, StatusCue StatusCue);

/// <summary>Coverage + remaining limits (reused from eligibility — never recomputed here). No clinical content.</summary>
public sealed record CoverageLine(string Category, decimal? AnnualLimit, decimal? RemainingLimit);

/// <summary>A contact point (editable in 15.4). Value is a phone/email/address, never clinical.</summary>
public sealed record MemberContact(Guid ContactId, string Kind, string Value, bool IsPrimary, string? PreferredChannel);

/// <summary>An appointment as the Call Centre sees it — existence + logistics ONLY. Type/time/branch/doctor+
/// specialty; NEVER the reason, notes, diagnosis, or any result.
///
/// <para><see cref="RowVersion"/> is emr's <c>xmin</c> optimistic-concurrency token, surfaced so the agent's
/// client can echo it back as <c>If-Match</c> on a reschedule/cancel. Without it every call-centre transition
/// ran unguarded: emr's 412-on-stale-write was implemented, forwarded, and never armed, so two agents holding
/// the same member's file could both act on an appointment one of them had already moved. It is a row token,
/// not member data — it carries nothing.</para></summary>
public sealed record MemberAppointment(
    Guid AppointmentId, string AppointmentType, string Status, DateTimeOffset ScheduledStart,
    string? BranchName, string? DoctorName, string? Specialty, bool CanReschedule, bool CanCancel,
    uint RowVersion = 0);

/// <summary>An open referral the agent can convert to a booking in one step (15.4).</summary>
public sealed record MemberReferral(string ReferralRef, string Status, string? RequestedSpecialty, DateTimeOffset? CreatedAt);

/// <summary>A follow-up due (from the appointment follow-up linkage) — bookable in one step.
///
/// <para><b>There is deliberately no <c>Reason</c> here.</b> It used to carry emr's free-text follow-up reason
/// verbatim, which is where "review biopsy result" lives — a clinical disclosure to a role that must never
/// receive one. The structural proof in <c>MemberProjectionTests</c> could not catch it: it scans property
/// names and a hand-populated instance, so a free-text field passed as long as the test author chose a benign
/// value. The agent's affordance is "book the follow-up", and <see cref="Specialty"/> plus
/// <see cref="DueDate"/> are what booking needs; the reason is the clinician's, not theirs.</para></summary>
public sealed record MemberFollowUp(Guid? OriginEncounterId, DateOnly? DueDate, string? Specialty);

/// <summary>The composed, projected Call Centre 360. Every section is logistics/benefit only — there is no
/// property anywhere in this graph that can hold clinical content.</summary>
public sealed record Member360(
    MemberIdentity Identity,
    IReadOnlyList<CoverageLine> Coverage,
    IReadOnlyList<MemberContact> Contacts,
    IReadOnlyList<MemberAppointment> Appointments,
    IReadOnlyList<MemberReferral> OpenReferrals,
    IReadOnlyList<MemberFollowUp> FollowUpsDue);
