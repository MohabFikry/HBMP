using System.Text.Json;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Api;

// Phase 19.8 — the plan and the policy as administrable records (design 57). The payer's shapes (19.7) are
// the template; what differs is documented where it differs, because a difference nobody explained reads as
// an inconsistency.

// ════════════════════════════════════════════════════════════════════════════════════════════════════════
// PLAN
// ════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What hangs off a plan. A plan's weight is not its own row — it is the versions authored against
/// it and the policies that sell it, and neither is visible from the catalogue list.</summary>
public sealed record PlanBookView(
    int VersionCount,
    int DraftCount,
    /// <summary>At most one, by construction — but projected as a count rather than a bool so a plan that
    /// somehow has two is visible as a fault instead of rendering as "yes".</summary>
    int ActiveCount,
    int SupersededCount,
    /// <summary>Policies that have attached a version of this plan. The number that decides whether
    /// withdrawing it is a catalogue tidy-up or a commercial event.</summary>
    int PolicyCount,
    int ActivePolicyCount,
    int MemberCount,
    int ActiveMemberCount,
    /// <summary>The window the plan is sellable across, derived from its versions: the earliest version
    /// start and the latest end (null = open-ended). Answers "is this product still current" without making
    /// the reader open the version list.</summary>
    DateOnly? FirstEffectiveFrom,
    DateOnly? LastEffectiveTo);

public sealed record PlanAdminView(
    Guid PlanId, string PlanCode, string NameEn, string NameAr, string? Description, string Category,
    string Status, string? StatusReason, DateTimeOffset? StatusChangedAt,
    DateTimeOffset UpdatedAt, string? UpdatedByName)
{
    public static PlanAdminView From(Plan p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.PlanId, p.PlanCode, p.NameEn, p.NameAr, p.Description, p.Category,
            p.Status.ToString(), p.StatusReason, p.StatusChangedAt, p.UpdatedAt, p.UpdatedByName);
    }
}

public sealed record PlanDetailView(PlanAdminView Plan, PlanBookView Book);

/// <summary>
/// An update carries no <c>planCode</c>, for the reason the payer's does not carry its own: the code is what
/// extracts, reconciliation files and the payer's systems join on, so it is replaceable rather than
/// correctable. The CATEGORY is editable — it describes the product, and nothing adjudicated refers to it.
/// </summary>
public sealed record UpdatePlan(string NameEn, string NameAr, string? Description, string Category);

public sealed record PlanHistoryEntryView(
    long HistoryId, string Operation, DateTimeOffset RecordedAt,
    string? ActorName, Guid? ActorId,
    string NameEn, string NameAr, string? Description, string Category,
    string Status, string? StatusReason)
{
    public static PlanHistoryEntryView From(PlanHistoryEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var doc = SnapshotJson.Parse(e.RowSnapshot);
        var r = doc?.RootElement;
        return new(
            e.HistoryId, e.Operation, e.RecordedAt,
            SnapshotJson.Str(r, "updated_by_name"), SnapshotJson.Uuid(r, "updated_by"),
            SnapshotJson.Str(r, "name_en") ?? "", SnapshotJson.Str(r, "name_ar") ?? "",
            SnapshotJson.Str(r, "description"), SnapshotJson.Str(r, "category") ?? "",
            SnapshotJson.Str(r, "status") ?? "", SnapshotJson.Str(r, "status_reason"));
    }
}

public sealed record PlanHistoryPage(Guid PlanId, IReadOnlyList<PlanHistoryEntryView> Entries);

// ════════════════════════════════════════════════════════════════════════════════════════════════════════
// POLICY (the contract)
// ════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Commercial terms on the contract. Withheld as a BLOCK from a caller who may not read contract
/// terms — <c>null</c> means restricted, not "not recorded", exactly as on the payer (19.7 §4.2).</summary>
public sealed record PolicyTermsView(Guid? PayerId, int? MaxMembers, Guid? PreviousPolicyId, string? Notes);

/// <summary>What is riding on the contract. Amounts follow <c>MayReadAmounts</c>; counts do not.</summary>
public sealed record PolicyBookView(
    int MemberCount,
    int ActiveMemberCount,
    int PlanCount,
    decimal? CommittedLimit,
    decimal? ConsumedValue,
    /// <summary>Members as a percentage of <c>maxMembers</c>, or null when the contract is uncapped. Survives
    /// a caller who may not read the cap itself: "this policy is at 96% of its ceiling" is operational.</summary>
    decimal? PercentOfCap);

public sealed record PolicyAdminView(
    Guid PolicyId, string PolicyNo, string Status, string? StatusReason, DateTimeOffset? StatusChangedAt,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    /// <summary><c>NotYetStarted | InForce | Ended</c> — projected, so the screen and the service cannot
    /// disagree about whether a contract's own window is still open.</summary>
    string WindowState,
    PolicyTermsView? Terms,
    DateTimeOffset UpdatedAt, string? UpdatedByName)
{
    public static PolicyAdminView From(Domain.Policy p, DateOnly today, bool mayReadContract)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.PolicyId, p.PolicyNo, p.Status.ToString(), p.StatusReason, p.StatusChangedAt,
            p.EffectiveFrom, p.EffectiveTo, p.WindowState(today).ToString(),
            mayReadContract ? new PolicyTermsView(p.PayerId, p.MaxMembers, p.PreviousPolicyId, p.Notes) : null,
            p.UpdatedAt, p.UpdatedByName);
    }
}

public sealed record PolicyDetailView(PolicyAdminView Policy, PolicyBookView Book);

/// <summary>
/// The policy number is absent for the same reason a payer code and a plan code are: claims, extracts and the
/// payer's own systems key on it. The WINDOW and the CAP are editable — a contract's dates and member ceiling
/// are exactly the terms that get renegotiated, and the alternative to editing them is a renewal, which
/// creates a different contract and is a different act.
/// </summary>
public sealed record UpdatePolicy(
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, int? MaxMembers, Guid? PayerId, string? Notes);

/// <summary>Suspend, resume or expire. The reason is REQUIRED — a contract found Suspended with no reason
/// preserves the fact and loses the decision, and the decision is what somebody needs when the payer calls
/// asking why their members are being turned away.</summary>
public sealed record ChangePolicyStatus(string Reason);

/// <summary>
/// What a status change actually did, with its blast radius.
///
/// <para>Suspending a policy is NOT refused when it has live members — unlike deactivating a payer, which is.
/// A payer is a catalogue row and cascading it would end cover nobody reviewed; suspending a contract IS the
/// operation, the thing that happens when a payer stops paying, and it necessarily affects live members.
/// Refusing it would be refusing the operation. So the count comes back instead, and the screen states the
/// impact in the confirmation rather than after the fact.</para>
/// </summary>
public sealed record PolicyStatusResult(PolicyAdminView Policy, int ActiveMembersAffected);

public sealed record PolicyHistoryEntryView(
    long HistoryId, string Operation, DateTimeOffset RecordedAt,
    string? ActorName, Guid? ActorId,
    string PolicyNo, string Status, string? StatusReason,
    DateOnly? EffectiveFrom, DateOnly? EffectiveTo,
    int? MaxMembers, Guid? PayerId)
{
    public static PolicyHistoryEntryView From(PolicyHistoryEntry e, bool mayReadContract)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var doc = SnapshotJson.Parse(e.RowSnapshot);
        var r = doc?.RootElement;
        return new(
            e.HistoryId, e.Operation, e.RecordedAt,
            SnapshotJson.Str(r, "updated_by_name"), SnapshotJson.Uuid(r, "updated_by"),
            SnapshotJson.Str(r, "policy_no") ?? "", SnapshotJson.Str(r, "status") ?? "",
            SnapshotJson.Str(r, "status_reason"),
            SnapshotJson.Date(r, "effective_from"), SnapshotJson.Date(r, "effective_to"),
            // The commercial half of a history row follows the same rule as the commercial half of the
            // policy: a reader who may not see today's cap must not be able to read last month's.
            mayReadContract ? SnapshotJson.Int(r, "max_members") : null,
            mayReadContract ? SnapshotJson.Uuid(r, "payer_id") : null);
    }
}

public sealed record PolicyHistoryPage(Guid PolicyId, IReadOnlyList<PolicyHistoryEntryView> Entries);

/// <summary>
/// Reads a trigger-written <c>row_snapshot</c>.
///
/// <para>Shared by three history projections. It was written once inside <see cref="PayerHistoryEntryView"/>
/// and copied nowhere: a snapshot reader that each history duplicates is three chances to disagree about what
/// an unparseable row means, and the answer has to be the same every time — return the entry with empty
/// fields, so one corrupt row cannot make the rest of a timeline unreadable.</para>
/// </summary>
internal static class SnapshotJson
{
    public static JsonDocument? Parse(string raw)
    {
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
    }

    public static JsonElement? Get(JsonElement? root, string name) =>
        root is { } r && r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(JsonElement? r, string n) => Get(r, n)?.GetString();
    public static Guid? Uuid(JsonElement? r, string n) => Guid.TryParse(Str(r, n), out var g) ? g : null;
    public static DateOnly? Date(JsonElement? r, string n) => DateOnly.TryParse(Str(r, n), out var d) ? d : null;
    public static decimal? Dec(JsonElement? r, string n) =>
        Get(r, n) is { ValueKind: JsonValueKind.Number } v ? v.GetDecimal() : null;
    public static int? Int(JsonElement? r, string n) =>
        Get(r, n) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : null;
}
