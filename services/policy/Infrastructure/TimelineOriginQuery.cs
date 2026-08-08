using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

/// <summary>Where a membership's history begins: either the projected enrolment entry, or — when none was ever
/// projected — the moment the record itself says it came into existence. Exactly one of the two is set.</summary>
public sealed record MembershipOrigin(TimelineEntry? Projected, DateTimeOffset? DerivedAt)
{
    public bool IsDerived => Projected is null;
}

/// <summary>
/// Phase 19.6e — the first line of the Logs tab.
///
/// <para><b>Why this is a query and not a `.Last()` on the page.</b> The timeline is newest-first and
/// cursor-paged, so the entry a reader most often wants — when this membership started, and who started it —
/// is the one guaranteed to be furthest from them. Resolving it separately means the log can open on it
/// however long the history has grown.</para>
///
/// <para><b>Why there is a derived case at all.</b> `MemberEnrolled` is only projected by the enrolment
/// command. Memberships created by bulk intake, by a migration, or before 19.3c have no such entry, and their
/// history began mid-sentence — the oldest line on a quarter of the dev records is a plan change, with nothing
/// saying the member was ever enrolled. The fallback invents nothing: it reads the append-only
/// <c>enrollment_event</c> row for the enrolment, or failing that the membership's own <c>CreatedAt</c>, and
/// leaves the actor empty rather than guessing at one. The caller marks the result as derived.</para>
/// </summary>
public static class TimelineOriginQuery
{
    public static async Task<MembershipOrigin?> ForMemberAsync(
        PolicyDbContext db, Guid enrollmentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var projected = await db.TimelineEntries.AsNoTracking()
            .Where(e => e.Scope == NoteScope.Member && e.ScopeRef == enrollmentId && e.EventType == "MemberEnrolled")
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.EntryId)
            .FirstOrDefaultAsync(ct);
        if (projected is not null) return new MembershipOrigin(projected, null);

        // Unknown membership → no origin. Deliberately NOT "now": a log that anchors an id it has never heard
        // of on the current clock is a log that answers a question nobody asked with a value nobody can check.
        var created = await db.Enrollments.AsNoTracking()
            .Where(e => e.EnrollmentId == enrollmentId)
            .Select(e => (DateTimeOffset?)e.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (created is null) return null;

        // The enrolment event is the better of the two: it is when the enrolment was DECIDED, which on a
        // back-dated or imported membership is not when the row happened to be written.
        var enrolled = await db.EnrollmentEvents.AsNoTracking()
            .Where(e => e.EnrollmentId == enrollmentId && e.EventType == EnrollmentEventType.Enrolled)
            .OrderBy(e => e.OccurredAt)
            .Select(e => (DateTimeOffset?)e.OccurredAt)
            .FirstOrDefaultAsync(ct);

        return new MembershipOrigin(null, enrolled ?? created.Value);
    }
}
