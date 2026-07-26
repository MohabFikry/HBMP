namespace Mersal.CallCentre.Domain;

// The call-centre member views (phase 15.2). MINIMUM-NECESSARY + CLINICAL-FREE by construction (design 37 §6,
// 11-permission-matrix): the shapes below CANNOT carry a diagnosis, result, prescription, note, vital, or
// examination detail — there is no field for them. Proven additionally by an authorization test over the
// serialized JSON. The Call Centre is MemberScoped, so appointments span ALL branches.

/// <summary>Pre-verification search hit — deliberately thin. ONLY a display name, the beneficiary id, and WHICH
/// identifier TYPES the agent may challenge on. No coverage, no contacts, no appointments, no history.</summary>
public sealed record MemberMatch(
    Guid BeneficiaryId,
    string DisplayName,
    string? MemberNo,
    IReadOnlyList<string> ChallengeableIdentifierTypes);

/// <summary>The pre-verification search response.</summary>
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
/// specialty; NEVER the reason, notes, diagnosis, or any result.</summary>
public sealed record MemberAppointment(
    Guid AppointmentId, string AppointmentType, string Status, DateTimeOffset ScheduledStart,
    string? BranchName, string? DoctorName, string? Specialty, bool CanReschedule, bool CanCancel);

/// <summary>An open referral the agent can convert to a booking in one step (15.4).</summary>
public sealed record MemberReferral(string ReferralRef, string Status, string? RequestedSpecialty, DateTimeOffset? CreatedAt);

/// <summary>A follow-up due (from the appointment follow-up linkage) — bookable in one step.</summary>
public sealed record MemberFollowUp(Guid? OriginEncounterId, string? Reason, DateOnly? DueDate, string? Specialty);

/// <summary>The composed, projected Call Centre 360. Every section is logistics/benefit only — there is no
/// property anywhere in this graph that can hold clinical content.</summary>
public sealed record Member360(
    MemberIdentity Identity,
    IReadOnlyList<CoverageLine> Coverage,
    IReadOnlyList<MemberContact> Contacts,
    IReadOnlyList<MemberAppointment> Appointments,
    IReadOnlyList<MemberReferral> OpenReferrals,
    IReadOnlyList<MemberFollowUp> FollowUpsDue);
