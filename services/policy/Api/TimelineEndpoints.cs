using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>One entry as the caller is entitled to see it. <c>DiffWithheld</c> is explicit for the same reason
/// a note's is: a withheld diff must render as "details restricted for your role", never as a blank row that
/// reads like nothing changed.</summary>
public sealed record TimelineEntryView(
    Guid EntryId, string Scope, Guid ScopeRef, DateTimeOffset OccurredAt,
    string EventType, string EventCategory,
    string? ActorUsername, string? ActorDisplay,
    string SummaryEn, string SummaryAr,
    string? ChangeDiff, bool DiffWithheld,
    string VisibilityClass, string SourceService, string? CorrelationId,
    Guid? TargetRef, string? TargetKind,
    /// <summary>True when this entry was READ OFF the record rather than projected from an event — see
    /// <see cref="TimelineEndpoints"/>' origin resolution. The client says so; a log that presents a derived
    /// row as a projected one is a log that cannot be trusted about the rest.</summary>
    bool Derived = false)
{
    public static TimelineEntryView For(TimelineEntry e, IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(e);
        var diff = TimelineProjection.ProjectDiff(e, roles);
        return new(e.EntryId, e.Scope.ToString(), e.ScopeRef, e.OccurredAt,
            e.EventType, e.EventCategory.ToString(),
            e.ActorUsername, e.ActorDisplay, e.SummaryEn, e.SummaryAr,
            diff, e.ChangeDiff is not null && diff is null,
            e.VisibilityClass.ToString(), e.SourceService, e.CorrelationId, e.TargetRef, e.TargetKind);
    }
}

/// <summary>
/// A page of history, plus the entry the history STARTS from.
///
/// <para><b>Why origin is separate from the entries.</b> The page is newest-first and cursor-paged, so the one
/// entry that is always worth seeing — the day this membership came into existence, and who put it there — is
/// the single entry guaranteed to be furthest from the reader. On a member with a year of activity it sat
/// behind however many "load older" clicks the record had earned. It is returned on the FIRST page only
/// (a cursor means the reader already has it) and is removed from <see cref="Entries"/> when it happens to
/// fall inside the same page, so nothing renders twice.</para>
/// </summary>
public sealed record TimelinePage(
    IReadOnlyList<TimelineEntryView> Entries,
    DateTimeOffset? NextCursor,
    TimelineEntryView? Origin = null);

/// <summary>
/// Phase 19.3c — "what happened to this policy / this member, when, and who did it" (design 38 §5c).
///
/// <para>Read-only by construction: there is no write endpoint here, because the timeline is a projection over
/// events that already happened. The one mutating operation is a full REBUILD, which discards derived data
/// and re-derives it — and produces byte-identical rows, so it can be verified rather than trusted.</para>
/// </summary>
public static class TimelineEndpoints
{
    public static void MapTimeline(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("policy:read"));

        MapRead(read, "/policies/{id:guid}/timeline", NoteScope.Policy);
        MapRead(read, "/enrollments/{id:guid}/timeline", NoteScope.Member);
        MapExport(read);
    }

    private static void MapRead(RouteGroupBuilder read, string route, NoteScope scope)
    {
        read.MapGet(route, async (Guid id, DateTimeOffset? from, DateTimeOffset? to, string? category,
            string? actor, string? eventType, DateTimeOffset? cursor, int? pageSize,
            PolicyDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var take = Math.Clamp(pageSize ?? 50, 1, 200);
            var q = db.TimelineEntries.AsNoTracking().Where(e => e.Scope == scope && e.ScopeRef == id);
            if (from is { } f) q = q.Where(e => e.OccurredAt >= f);
            if (to is { } t) q = q.Where(e => e.OccurredAt <= t);
            if (category is not null && Enum.TryParse<TimelineCategory>(category, out var c))
                q = q.Where(e => e.EventCategory == c);
            if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(e => e.ActorUsername == actor);
            if (!string.IsNullOrWhiteSpace(eventType)) q = q.Where(e => e.EventType == eventType);
            // Cursor by occurred_at, newest first. Deliberately not offset paging: a history grows at the top,
            // so an offset walks over entries that shifted down between pages.
            if (cursor is { } cur) q = q.Where(e => e.OccurredAt < cur);

            var rows = await q.OrderByDescending(e => e.OccurredAt).ThenBy(e => e.EntryId).Take(take + 1).ToListAsync(ct);
            var hasMore = rows.Count > take;
            if (hasMore) rows.RemoveAt(rows.Count - 1);

            var views = rows.Select(e => TimelineEntryView.For(e, principal.Roles)).ToList();

            // The first entry is always the record's creation. Only on the first page — a caller holding a
            // cursor has already been given it — and never twice: if the creation entry is also inside this
            // page (a short history, or the last page of a long one), it is shown as the origin and dropped
            // from the body.
            TimelineEntryView? origin = null;
            if (scope == NoteScope.Member && cursor is null)
            {
                origin = await OriginAsync(db, id, principal.Roles, ct);
                if (origin is not null) views.RemoveAll(v => v.EntryId == origin.EntryId);
            }

            // Reading a member's timeline is a PHI read: it names their care, their claims and who accessed
            // their record. Audited whether or not any diff was disclosed.
            if (scope == NoteScope.Member)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "entity_timeline", EntityId = $"{scope}:{id}", Action = AuditAction.Read,
                    ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                    TenantId = principal.TenantId,
                    DecisionOutcome = views.Any(v => v.DiffWithheld) ? "partial-withheld" : "disclosed",
                }, ct);
            }

            return Results.Ok(new TimelinePage(views, hasMore ? rows[^1].OccurredAt : null, origin));
        });
    }

    /// <summary>
    /// Where this membership's history begins, as the caller may see it.
    ///
    /// <para>The resolution rules live in <see cref="TimelineOriginQuery"/>. What is decided HERE is how each
    /// of its two answers renders: a projected entry goes through the same role projection every other entry
    /// does, and a derived one is given the enrolment's own summary, no actor — nothing is invented to sign it
    /// with — and <see cref="TimelineEntryView.Derived"/> set, so the reader is told which kind of line they
    /// are looking at.</para>
    /// </summary>
    private static async Task<TimelineEntryView?> OriginAsync(
        PolicyDbContext db, Guid enrollmentId, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        var origin = await TimelineOriginQuery.ForMemberAsync(db, enrollmentId, ct);
        if (origin is null) return null;
        if (origin.Projected is { } projected) return TimelineEntryView.For(projected, roles);

        // The id is the enrolment's own — stable across reads (so a client keyed on it does not remount the
        // row) and impossible to confuse with a projected entry id, which is a hash of a source event.
        return new TimelineEntryView(
            enrollmentId, NoteScope.Member.ToString(), enrollmentId, origin.DerivedAt!.Value,
            "MemberEnrolled", TimelineCategory.Enrolment.ToString(),
            ActorUsername: null, ActorDisplay: null,
            SummaryEn: "Member enrolled", SummaryAr: "تم تسجيل العضو",
            ChangeDiff: null, DiffWithheld: false,
            VisibilityClass: NoteVisibility.Administrative.ToString(), SourceService: "policy",
            CorrelationId: null, TargetRef: null, TargetKind: null, Derived: true);
    }

    private static void MapExport(RouteGroupBuilder read)
    {
        // Column-allow-listed, and clinical diffs are NEVER in an export whatever the caller's role. An export
        // leaves the platform's controls behind — it becomes a file on somebody's laptop — so it carries the
        // narrower rule, not the caller's own entitlement.
        read.MapGet("/timeline/export", async (string scope, Guid scopeRef, PolicyDbContext db,
            IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            if (!Enum.TryParse<NoteScope>(scope, out var parsedScope))
                return ProblemResults.Invalid("UNKNOWN_SCOPE", $"'{scope}' is not a timeline scope.");

            var rows = await db.TimelineEntries.AsNoTracking()
                .Where(e => e.Scope == parsedScope && e.ScopeRef == scopeRef)
                .OrderByDescending(e => e.OccurredAt).ToListAsync(ct);

            var csv = new System.Text.StringBuilder();
            csv.AppendLine("occurredAtUtc,category,eventType,actor,summaryEn,summaryAr");
            foreach (var e in rows)
            {
                csv.Append(e.OccurredAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                   .Append(e.EventCategory).Append(',')
                   .Append(Csv(e.EventType)).Append(',')
                   .Append(Csv(e.ActorUsername ?? "")).Append(',')
                   .Append(Csv(e.SummaryEn)).Append(',')
                   .Append(Csv(e.SummaryAr)).AppendLine();
            }

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "entity_timeline", EntityId = $"{parsedScope}:{scopeRef}", Action = AuditAction.Export,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId, DecisionOutcome = $"rows={rows.Count}",
            }, ct);

            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
                "text/csv", $"timeline-{parsedScope}-{scopeRef}.csv");
        });
    }

    private static string Csv(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
