using Mersal.Eligibility.Infrastructure;

namespace Mersal.Eligibility.Api;

// The reception result card (US-010, 11-permission-matrix, 13-ux-flows). MINIMUM-NECESSARY only:
// identity + coverage + remaining limits + a visit-history SUMMARY. NO diagnoses / notes / orders /
// prescriptions / results / vitals — the shape itself cannot carry EMR data.

/// <summary>Non-color status semantics for the UI (21-accessibility): hue is never the only signal.</summary>
public sealed record StatusSemantics(string Label, string Icon, string Shape, string Tone)
{
    public static StatusSemantics For(string status) => status switch
    {
        "Active" => new("Active", "check-circle", "circle", "positive"),
        "Suspended" => new("Suspended", "pause", "square", "caution"),
        "Expired" => new("Expired", "clock-x", "diamond", "caution"),
        "Blocked" => new("Blocked", "ban", "octagon", "critical"),
        "Inactive" => new("Inactive", "minus-circle", "square", "neutral"),
        _ => new("Pending", "hourglass", "triangle", "neutral"),
    };
}

public sealed record ReceptionIdentity(Guid BeneficiaryId, string? MemberNo, string DisplayName, string Status, StatusSemantics StatusSemantics);
public sealed record VisitHistorySummary(int Count, DateOnly? LastVisitDate, string? LastVisitType);

public sealed record ReceptionResultCard(
    ReceptionIdentity Identity,
    IReadOnlyList<string> Coverage,
    IReadOnlyList<RemainingLimit> RemainingLimits,
    VisitHistorySummary VisitHistory)
{
    public static ReceptionResultCard From(ReceptionDocument d) => new(
        new ReceptionIdentity(d.BeneficiaryId, d.MemberNo,
            $"{d.GivenName} {d.FamilyName}".Trim(), d.Status, StatusSemantics.For(d.Status)),
        d.ActiveCategories,
        d.RemainingLimits,
        new VisitHistorySummary(d.VisitCount, d.LastVisitDate, d.LastVisitType));
}

/// <summary>The reception search response — result cards, or an empty state with guidance.</summary>
/// <param name="Count">How many cards are IN THIS RESPONSE. Not how many matched — see
/// <paramref name="Truncated"/>.</param>
/// <param name="Truncated">
/// True when the search matched more than the page holds.
/// </param>
/// <remarks>
/// <para><b>Why the flag exists.</b> The search takes the first 25 rows and reported <c>Count</c> as the
/// length of that page, so a term matching forty people answered "25 matches" and said nothing about the
/// other fifteen. An operator picking a patient from that list is choosing from a truncated set presented as
/// the complete one — and the person they are looking for may simply not be on it, with nothing on screen to
/// suggest looking further.</para>
///
/// <para>A COUNT of the full match set is deliberately not returned. It would cost a second query on every
/// keystroke-driven search for a number nobody acts on: the answer to "too many" is always to narrow the
/// term, and "more than 25" says that as well as "137" does.</para>
/// </remarks>
public sealed record ReceptionSearchResponse(
    string Query,
    int Count,
    IReadOnlyList<ReceptionResultCard> Results,
    string? EmptyStateHint,
    bool Truncated = false);

// ================================================================ VERIFIED LOOKUP (33.9)
//
// The eligibility screen used to search on one free-text box and check the FIRST hit. "Ahmed" matched every
// Ahmed on the platform, one of them was chosen by whatever order the database returned, and the plan, the
// remaining cap and the visit verdict on screen belonged to a person nobody had picked — with nothing on the
// card to say there had been others. This pair replaces that path: an identifier that resolves to exactly one
// member, and a name that has to agree with it.

/// <summary>What the desk was given: something the beneficiary presented, and enough of their name to
/// corroborate it. POST rather than a query string — a national ID and a name in a URL end up in the
/// gateway's access log, the browser's history and every proxy in between.</summary>
public sealed record ReceptionVerifyRequest(string? Identifier, string? Name);

/// <summary>
/// The answer, discriminated on <see cref="Verified"/>.
/// </summary>
/// <remarks>
/// <para><b>The refusal carries no identity.</b> Not the name on file, not the member number, not the
/// membership status — nothing but a reason code. An endpoint that answered "no, that card belongs to
/// someone else called X" would hand out the name behind any card number to anyone holding one, which is a
/// worse disclosure than the defect it replaces.</para>
///
/// <para><b>Not-found and name-mismatch stay distinguishable.</b> They are different situations at the desk
/// and lead to different actions — re-read the digits, or ask the person to repeat their name — and
/// collapsing them would leave an operator unable to tell a typo from the wrong person. The cost is that a
/// holder of a card number learns the card is registered. That is a real disclosure and it is the smaller
/// one; the mismatch is audited at High severity so a run of them across different numbers is visible as the
/// fishing pattern it would be.</para>
/// </remarks>
public sealed record ReceptionVerifyResponse(bool Verified, string? Reason, ReceptionResultCard? Card)
{
    /// <summary>Nothing on file matches that identifier.</summary>
    public const string NotFound = "not-found";
    /// <summary>The identifier resolves, and the name given does not agree with the record.</summary>
    public const string NameMismatch = "name-mismatch";
    /// <summary>The name given is too short to narrow anything — see IdentityCorroboration.MinimumFragment.</summary>
    public const string NameTooShort = "name-too-short";

    public static ReceptionVerifyResponse Refused(string reason) => new(false, reason, null);
    public static ReceptionVerifyResponse Of(ReceptionResultCard card) => new(true, null, card);
}
