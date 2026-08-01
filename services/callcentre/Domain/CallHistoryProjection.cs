using System.Globalization;
using Mersal.Authz;
using Mersal.Time;

namespace Mersal.CallCentre.Domain;

// Phase 20.3b — the call-history section's rows, projected to Full / Operational / Meta, and the server-generated
// clipboard block (design 39 §5b).
//
// THE ORDER OF OPERATIONS IS THE CONTROL. The row is narrowed FIRST, and the clipboard text is then built from
// THAT narrowed row — never from the source entity. It would be one line shorter to format the copy block from
// the interaction directly, and that one line is the whole vulnerability: a Meta-level viewer would get a
// summary in their clipboard while the JSON beside it correctly omitted one, and nothing on screen would show
// it. Deriving the text from the served projection means there is nothing to strip, because the summary was
// never in scope when the string was built.

/// <summary>A verification attempt as the FULL projection shows it: the result and WHICH identifier types were
/// confirmed — never the values, which have never been stored (phase 15's privacy rule).</summary>
public sealed record CallVerificationView(string Result, IReadOnlyList<string> IdentifierTypes);

/// <summary>Something the call produced: an appointment booked or moved, a contact change, a complaint.</summary>
public sealed record LinkedArtifactView(string Type, string Ref, string? Action);

/// <summary>
/// One call-history row, already narrowed to the caller's level. A field the level dropped is <c>null</c>, and
/// callcentre-service serializes with nulls omitted — so a Meta row has no <c>summary</c> key at all.
/// </summary>
public sealed record ProjectedCallRow(
    string CallRef,
    string Direction,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    string? BranchCode,
    string? AgentDisplayName,
    string? ReasonCode,
    string? Outcome,
    CallVerificationView? Verification,
    string? Summary,
    bool SummaryEdited,
    IReadOnlyList<LinkedArtifactView>? LinkedArtifacts,
    string CopyText);

/// <summary>Everything the projector needs about one interaction that does not live on the entity itself.</summary>
public sealed record CallRowSource(
    CallInteraction Interaction,
    string? MemberRef,
    string? AgentDisplayName,
    string? BranchCode,
    CallerVerification? LatestVerification,
    IReadOnlyList<LinkedArtifactView> LinkedArtifacts);

/// <summary>Design 39 §5b, as a pure function so the API and the tests share one implementation.</summary>
public static class CallHistoryProjection
{
    public const string English = "en";
    public const string Arabic = "ar";

    /// <summary>Narrow one interaction to a level, then build its clipboard block from the result.</summary>
    public static ProjectedCallRow Project(CallRowSource source, CallHistoryLevel level, string lang = English)
    {
        ArgumentNullException.ThrowIfNull(source);
        var i = source.Interaction;

        var duration = i.EndedAt is { } ended
            ? (int)Math.Max(0, (ended - i.StartedAt).TotalSeconds)
            : (int?)null;

        // Meta: direction, date/time, reason code, outcome. Nothing else — enough for finance to see that a
        // billing call happened, without the narrative (design 39 §5b).
        // Operational: + duration, branch, summary, linked artefacts. NO verification detail, NO agent notes.
        // Full: + verification detail and the agent who handled it.
        var isMeta = level <= CallHistoryLevel.Meta;
        var isFull = level >= CallHistoryLevel.Full;

        var row = new ProjectedCallRow(
            i.CallRef,
            i.Direction.ToString(),
            i.StartedAt,
            isMeta ? null : i.EndedAt,
            isMeta ? null : duration,
            isMeta ? null : source.BranchCode,
            isFull ? source.AgentDisplayName : null,
            i.ReasonCode?.ToString(),
            i.Outcome?.ToString(),
            isFull && source.LatestVerification is { } v
                ? new CallVerificationView(v.Result.ToString(), v.VerifiedIdentifierTypes)
                : null,
            isMeta ? null : i.Summary,
            i.SummaryEditedAt is not null,
            isMeta ? null : source.LinkedArtifacts,
            // Placeholder — replaced below from the narrowed row, never from `i`.
            string.Empty);

        return row with { CopyText = CopyText(row, source.MemberRef, lang) };
    }

    /// <summary>
    /// The clipboard block, built ONLY from the fields present on the projected row.
    ///
    /// <para>It always carries provenance — member ref, call ref, direction, timestamp — so a pasted summary can
    /// be traced back and cannot be mistaken for a clinical note. It never carries verification detail or the
    /// agent's working notes, at any level: widening the audience for call history must not widen the audience
    /// for whatever an agent typed mid-call.</para>
    /// </summary>
    public static string CopyText(ProjectedCallRow row, string? memberRef, string lang = English)
    {
        ArgumentNullException.ThrowIfNull(row);
        var ar = string.Equals(lang, Arabic, StringComparison.OrdinalIgnoreCase);

        var when = TimeZoneInfo.ConvertTime(row.StartedAt, BusinessCalendar.CairoZone)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var head = $"[{Direction(row.Direction, ar)}] {when}";
        if (row.DurationSeconds is { } seconds) head += $" ({seconds / 60}m {seconds % 60}s)";
        if (!string.IsNullOrWhiteSpace(row.BranchCode)) head += $" · {row.BranchCode}";
        if (!string.IsNullOrWhiteSpace(row.AgentDisplayName))
            head += $" · {(ar ? "الموظف" : "Agent")}: {row.AgentDisplayName}";

        var lines = new List<string>
        {
            head,
            $"{(ar ? "العضو" : "Member")}: {memberRef ?? "—"} · {(ar ? "المرجع" : "Ref")}: {row.CallRef}",
            $"{(ar ? "السبب" : "Reason")}: {row.ReasonCode ?? "—"} · {(ar ? "النتيجة" : "Outcome")}: {row.Outcome ?? "—"}",
        };

        // The one conditional line. At Meta there is no summary ON THE ROW, so there is none here either — not
        // because this method checked the level, but because it only ever reads what it was given.
        if (!string.IsNullOrWhiteSpace(row.Summary)) lines.Add(row.Summary);

        return string.Join('\n', lines);
    }

    /// <summary>"Copy all visible" — the same blocks, joined, with a blank line between calls.</summary>
    public static string CopyAll(IEnumerable<ProjectedCallRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return string.Join("\n\n", rows.Select(r => r.CopyText));
    }

    /// <summary>Translated, not transliterated — the direction word is the accessible cue, so it has to read
    /// naturally in both languages (design 39 §5b, 0B four-cue rule).</summary>
    private static string Direction(string direction, bool ar) => (direction, ar) switch
    {
        ("Inbound", true) => "وارد",
        ("Outbound", true) => "صادر",
        _ => direction,
    };
}

/// <summary>
/// When a summary is REQUIRED (design 39 §5b).
///
/// <para>Required at close for every outcome except <see cref="CallOutcome.Abandoned"/> — an abandoned call has
/// nothing to account for, and demanding one would train agents to type "abandoned" into the field that other
/// roles read. Every other outcome ended in something, and the summary is that something.</para>
/// </summary>
public static class CallSummaryRules
{
    /// <summary>Length cap, so a summary stays a summary rather than becoming a second notes field.</summary>
    public const int MaxLength = 500;

    /// <summary>Cap on the agent's working notes. `summary` was capped in both the API and the column
    /// (varchar(500)); `notes` was validated in neither and stored as bare `text`, so the one field on this
    /// aggregate that is unbounded is also the free-text one an agent types under time pressure. Generous
    /// enough that no real call hits it, bounded so a client fault cannot write a megabyte per call.</summary>
    public const int MaxNotesLength = 4000;

    public static bool IsRequiredAtClose(CallOutcome? outcome) =>
        outcome is not null && outcome != CallOutcome.Abandoned;

    /// <summary>Whether a close may proceed. Returns the failure message, or null when it may.</summary>
    public static string? Validate(CallOutcome? outcome, string? summary)
    {
        if (!IsRequiredAtClose(outcome)) return null;
        if (string.IsNullOrWhiteSpace(summary))
            return $"A call summary is required when closing with outcome '{outcome}'. " +
                   "It is what another role reads later to understand what this call was about and what was done.";
        return summary.Length > MaxLength
            ? $"A call summary is capped at {MaxLength} characters; got {summary.Length}."
            : null;
    }
}
