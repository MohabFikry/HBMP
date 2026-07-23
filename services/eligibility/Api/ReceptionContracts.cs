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
public sealed record ReceptionSearchResponse(
    string Query,
    int Count,
    IReadOnlyList<ReceptionResultCard> Results,
    string? EmptyStateHint);
