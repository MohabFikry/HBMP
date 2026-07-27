using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.3 — notes on policy and member (design 38 §5).
///
/// A note is a signed statement about a person's case. It is written once, attributed to whoever wrote it at
/// the moment they wrote it, and can be withdrawn but never erased or edited. Those constraints are not
/// bureaucratic: this surface is where an officer records why an exception was granted, why a complaint was
/// upheld, why someone was refused — and it is read back in disputes, months later, by people who were not
/// there.
///
/// <para>Reading a clinical or restricted note is itself audited, because who looked at PHI is part of the
/// record. Writing and cancelling are always audited.</para>
/// </summary>
public static class NoteEndpoints
{
    public static void MapNotes(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("note:read"));
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("note:write"));

        MapCreate(write, "/policies/{id:guid}/notes", NoteScope.Policy);
        MapCreate(write, "/enrollments/{id:guid}/notes", NoteScope.Member);
        MapList(read, "/policies/{id:guid}/notes", NoteScope.Policy);
        MapList(read, "/enrollments/{id:guid}/notes", NoteScope.Member);
        MapCancel(write);
        MapPinning(write);
    }

    // ---- Create ------------------------------------------------------------------------------------------
    private static void MapCreate(RouteGroupBuilder group, string route, NoteScope scope)
    {
        group.MapPost(route, async (Guid id, CreateNote req, PolicyDbContext db, PolicyGate gate,
            IHbmpPrincipalAccessor me, IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            if (string.IsNullOrWhiteSpace(req.Body))
                return ProblemResults.Invalid("BODY_REQUIRED", "A note must have a body.");
            if (!Enum.TryParse<NoteType>(req.NoteType, out var noteType))
                return ProblemResults.Invalid("UNKNOWN_NOTE_TYPE", $"'{req.NoteType}' is not a note type.");
            if (!Enum.TryParse<NoteVisibility>(req.VisibilityClass, out var visibility))
                return ProblemResults.Invalid("UNKNOWN_VISIBILITY_CLASS", $"'{req.VisibilityClass}' is not a visibility class.");

            // The scope target must exist, or the note is filed against nothing and never surfaces again.
            var exists = scope == NoteScope.Policy
                ? await db.Policies.AnyAsync(p => p.PolicyId == id && !p.IsDeleted, ct)
                : await db.Enrollments.AnyAsync(e => e.EnrollmentId == id && !e.IsDeleted, ct);
            if (!exists) return NotFound();

            // THE SIGNATURE. Taken from the token principal and snapshotted — never from the request body,
            // which would let a caller sign as somebody else, and never as a join, which would rewrite the
            // signature when that person is renamed or de-provisioned.
            var now = clock.GetUtcNow();
            var note = new Note
            {
                NoteId = Guid.NewGuid(), Scope = scope, ScopeRef = id,
                NoteType = noteType, Body = req.Body.Trim(), VisibilityClass = visibility,
                AuthoredByUserId = SubjectId(principal) ?? Guid.Empty,
                AuthoredByUsername = principal.Subject ?? "unknown",
                AuthoredByDisplay = Display(principal),
                AuthoredAt = now,
                Status = NoteStatus.Active, Pinned = req.Pinned, SupersedesNoteId = req.SupersedesNoteId,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Notes.Add(note);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "note", EntityId = note.NoteId.ToString(), Action = AuditAction.Create,
                ActorUserId = principal.Subject, TenantId = principal.TenantId,
                FieldClasses = [visibility.ToString().ToLowerInvariant()],
            }, ct);
            await outbox.EnqueueAsync("NoteAdded", "policy.events", new
            {
                tenantId = note.TenantId, noteId = note.NoteId, scope = scope.ToString(), scopeRef = id,
                noteType = noteType.ToString(), visibilityClass = visibility.ToString(),
            }, ct);

            _ = gate;   // authorization is the scope on the group; notes carry no further ABAC resource
            return Results.Created($"/api/v1/notes/{note.NoteId}",
                NoteView.For(note, principal.Roles, SubjectId(principal), hasSupervisorScope: false));
        });
    }

    // ---- List --------------------------------------------------------------------------------------------
    private static void MapList(RouteGroupBuilder group, string route, NoteScope scope)
    {
        group.MapGet(route, async (Guid id, string? status, string? type, PolicyDbContext db,
            IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var q = db.Notes.AsNoTracking().Where(n => n.Scope == scope && n.ScopeRef == id);
            if (status is not null && Enum.TryParse<NoteStatus>(status, out var s)) q = q.Where(n => n.Status == s);
            if (type is not null && Enum.TryParse<NoteType>(type, out var t)) q = q.Where(n => n.NoteType == t);

            // Pinned first, then newest first (design 38 §5.8). Cancelled notes are NOT filtered out by
            // default: they stay visible, struck through, because a withdrawn note is information and a gap
            // where one used to be is not.
            var rows = await q.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.AuthoredAt).ToListAsync(ct);

            var userId = SubjectId(principal);
            var supervises = principal.HasScope("policy:supervise");
            var views = rows.Select(n => NoteView.For(n, principal.Roles, userId, supervises)).ToList();

            // Reading clinical or restricted material is a PHI read and is audited — including when the body
            // was WITHHELD, because an attempt to reach restricted content is exactly what a review looks for.
            var sensitive = rows.Where(n => n.ReadIsAuditable).ToList();
            if (sensitive.Count > 0)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "note", EntityId = $"{scope}:{id}", Action = AuditAction.Read,
                    ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                    TenantId = principal.TenantId,
                    DecisionOutcome = views.Any(v => v.BodyWithheld) ? "partial-withheld" : "disclosed",
                    FieldClasses = [.. sensitive.Select(n => n.VisibilityClass.ToString().ToLowerInvariant()).Distinct()],
                }, ct);
            }

            return Results.Ok(views);
        });
    }

    // ---- Cancel ------------------------------------------------------------------------------------------
    private static void MapCancel(RouteGroupBuilder write)
    {
        write.MapPost("/notes/{id:guid}/cancel", async (Guid id, CancelNote req, PolicyDbContext db,
            IHbmpPrincipalAccessor me, IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            // MANDATORY. A cancellation is the withdrawal of a signed statement; without a reason the record
            // reads as "someone took this back" and nothing more.
            if (string.IsNullOrWhiteSpace(req.Reason))
                return ProblemResults.Invalid("REASON_REQUIRED", "A reason is required to cancel a note.");

            var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == id, ct);
            if (note is null) return NotFound();
            if (note.Status == NoteStatus.Cancelled)
                return ProblemResults.Conflict("ALREADY_CANCELLED", "This note is already cancelled.");

            var userId = SubjectId(principal);
            var supervises = principal.HasScope("policy:supervise");
            if (!note.MayBeCancelledBy(userId, supervises))
            {
                // Audited. Someone attempting to withdraw a colleague's signed note without supervisory
                // authority is precisely the event a later review wants to find.
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "note", EntityId = id.ToString(), Action = AuditAction.StateChange,
                    ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                    TenantId = principal.TenantId,
                    DecisionOutcome = "denied", DecisionReasonCode = "not-author-nor-supervisor",
                }, ct);
                return GateResults.Forbidden("urn:hbmp:note-cancel-denied",
                    detail: "Only the author of a note or a supervisor may cancel it.",
                    reason: "not-author-nor-supervisor");
            }

            var now = clock.GetUtcNow();
            note.Status = NoteStatus.Cancelled;
            note.CancelledByUserId = userId;
            note.CancelledByUsername = principal.Subject;
            note.CancelledAt = now;
            note.CancellationReason = req.Reason.Trim();
            note.UpdatedAt = now;
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "note", EntityId = id.ToString(), Action = AuditAction.StateChange,
                ActorUserId = principal.Subject, TenantId = principal.TenantId,
                DecisionOutcome = "cancelled", DecisionReasonCode = req.Reason,
                FieldClasses = [note.VisibilityClass.ToString().ToLowerInvariant()],
            }, ct);
            await outbox.EnqueueAsync("NoteCancelled", "policy.events", new
            {
                tenantId = note.TenantId, noteId = id, scope = note.Scope.ToString(), scopeRef = note.ScopeRef,
                cancelledBy = principal.Subject, reason = req.Reason,
            }, ct);

            // The cancelled note is RETURNED, body and all — it stays visible, struck through.
            return Results.Ok(NoteView.For(note, principal.Roles, userId, supervises));
        });
    }

    // ---- Pin / unpin -------------------------------------------------------------------------------------
    private static void MapPinning(RouteGroupBuilder write)
    {
        // Pinning changes no content and no signature, so it is the one mutation open to any note writer.
        foreach (var (suffix, pinned) in new[] { ("pin", true), ("unpin", false) })
        {
            write.MapPost($"/notes/{{id:guid}}/{suffix}", async (Guid id, PolicyDbContext db,
                IHbmpPrincipalAccessor me, IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
            {
                var principal = me.Principal;
                if (principal is null) return GateResults.Unauthenticated();
                var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == id, ct);
                if (note is null) return NotFound();

                note.Pinned = pinned;
                note.UpdatedAt = clock.GetUtcNow();
                if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "note", EntityId = id.ToString(), Action = AuditAction.Update,
                    ActorUserId = principal.Subject, TenantId = principal.TenantId,
                    DecisionOutcome = pinned ? "pinned" : "unpinned",
                }, ct);
                return Results.Ok(NoteView.For(note, principal.Roles, SubjectId(principal),
                    principal.HasScope("policy:supervise")));
            });
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static Guid? SubjectId(HbmpPrincipal p) => Guid.TryParse(p.Subject, out var id) ? id : null;

    /// <summary>The display name to sign with. Falls back to the subject rather than to empty: an unsigned
    /// note is worse than one signed with a username.</summary>
    private static string Display(HbmpPrincipal p) =>
        string.IsNullOrWhiteSpace(p.DisplayName) ? p.Subject ?? "unknown" : p.DisplayName!;

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    /// <summary>The append-only trigger raises P0001 for any attempt to rewrite a body or a signature. It
    /// should be unreachable through these endpoints — none of them writes those columns — so it surfacing
    /// here means some OTHER path tried, and the message says exactly which rule it hit.</summary>
    private static async Task<IResult?> SaveOrConflict(PolicyDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg
                                           && pg.SqlState is "P0001" or "23514" or "23505")
        {
            var pgEx = (Npgsql.PostgresException)ex.InnerException!;
            return pgEx.SqlState switch
            {
                "P0001" => ProblemResults.Conflict("NOTE_APPEND_ONLY", pgEx.MessageText),
                "23514" => ProblemResults.Unprocessable("CHECK_VIOLATION", pgEx.MessageText),
                _ => ProblemResults.Conflict("DUPLICATE_KEY", "That note already exists."),
            };
        }
    }
}
