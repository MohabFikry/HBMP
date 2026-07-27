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
    Guid? TargetRef, string? TargetKind)
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

public sealed record TimelinePage(IReadOnlyList<TimelineEntryView> Entries, DateTimeOffset? NextCursor);

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

            return Results.Ok(new TimelinePage(views, hasMore ? rows[^1].OccurredAt : null));
        });
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
