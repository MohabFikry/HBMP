using Mersal.Amendment;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>Write a note on a line. <paramref name="Visibility"/> defaults to <c>ToFulfiller</c> — the common
/// case is an instruction meant to be read.</summary>
public sealed record WriteNoteRequest(string Body, string? Visibility);

public sealed record CancelNoteRequest(string Reason);

/// <summary>
/// What a reader receives. <b>The body is absent, not empty, when they may not read it</b> — and a note they
/// may not read is not returned at all, so the shape carries no trace of it.
/// </summary>
public sealed record NoteResponse(
    Guid NoteId, Guid OrderLineId, string Visibility, string Body,
    string AuthorDisplayName, DateTimeOffset AuthoredAt, string Status,
    DateTimeOffset? CancelledAt, string? CancelReason);

/// <summary>
/// 30.5b — notes on an order line (design 46 §7b). The doc-38 notes model on a different subject: append-only,
/// signed, cancellable but never deletable, class-projected.
///
/// <para><b>Adding a note is not an amendment.</b> Nothing here touches <c>order_line</c>: no supersede, no
/// <c>version_no</c> bump, no authorisation change. Conflating the two would send every "fasting sample" back
/// to the approval queue.</para>
///
/// <para><b>Sensitivity is inherited, not re-decided.</b> A note on a restricted examination is gated by that
/// LINE's sensitivity through the same <c>SensitiveDisclosure</c> rule the results path uses — otherwise the
/// note becomes the gap in a gate the result itself passes through.</para>
/// </summary>
public static class NoteEndpoints
{
    public static void MapOrderNotes(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/investigation-orders").RequireAuthorization();

        // ---- Read the notes on a line ------------------------------------------------------------------
        v1.MapGet("/{orderId:guid}/lines/{lineId:guid}/notes", async Task<IResult> (
            Guid orderId, Guid lineId, HttpRequest http, OrdersDbContext db, OrdersGate gate,
            IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null) return NotFound();
            var line = order.Lines.FirstOrDefault(l => l.OrderLineId == lineId);
            if (line is null) return NotFound();

            if (await gate.CheckAsync(OrdersPolicies.Read, orderId.ToString(), order.BeneficiaryId,
                    http.Headers.Authorization.ToString(), ct) is { } denied) return denied;

            var reader = ReaderFor(me.Principal, order);

            // SENSITIVITY IS INHERITED. A note on a mental-health investigation must not be readable by
            // someone who cannot read the result — the note would be the gap in the gate (design 46 §7b).
            // The SAME rule the results path applies (ServiceHistory.cs): the author of the order always
            // sees their own, and anyone else needs the sensitivity to be unrestricted for them.
            var isAuthor = string.Equals(order.CreatedBy, me.Principal?.Subject, StringComparison.Ordinal);
            if (SensitiveDisclosure.IsRestricted(line.SensitivityLevel.ToString(), callerHasAccess: isAuthor))
                return Results.Problem(statusCode: 403, title: "note-restricted",
                    type: "urn:hbmp:note-restricted",
                    detail: "This line carries a restricted sensitivity, and its notes inherit it.");

            // The chain, not the row: a note written on v1 is about the clinical intent, which survives the
            // amendment, so it stays visible on v2.
            var notes = await db.OrderNotes.AsNoTracking()
                .Where(n => n.RootLineId == line.RootLineId)
                .OrderByDescending(n => n.AuthoredAt).ToListAsync(ct);

            // THE PROJECTION. Filtered before serialization, so a note the caller may not read never reaches
            // the payload at all — "the screen does not show it" is not a control.
            var visible = NoteAudience
                .Readable(notes, n => Enum.Parse<NoteVisibility>(n.Visibility), reader)
                .Select(n => new NoteResponse(
                    n.NoteId, n.SubjectId, n.Visibility, n.Body, n.AuthorDisplayName, n.AuthoredAt,
                    n.Status, n.CancelledAt, n.CancelReason))
                .ToList();

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "order_note", EntityId = lineId.ToString(), Action = AuditAction.Read,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Allow",
                DecisionReasonCode = $"reader:{reader};notes:{visible.Count}", FieldClasses = ["phi"],
            }, ct);

            return Results.Ok(visible);
        }).RequireAuthorization(HbmpPolicies.Scope("orders:read"));

        // ---- Write one --------------------------------------------------------------------------------
        v1.MapPost("/{orderId:guid}/lines/{lineId:guid}/notes", async Task<IResult> (
            Guid orderId, Guid lineId, WriteNoteRequest req, HttpRequest http, OrdersDbContext db,
            OrdersGate gate, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body))
                return Results.Problem(statusCode: 422, title: "empty-note", type: "urn:hbmp:empty-note");
            if (req.Body.Length > 500)
                return Results.Problem(statusCode: 422, title: "note-too-long", type: "urn:hbmp:note-too-long",
                    detail: "A note is an operational instruction, not a clinical record. Clinical findings "
                          + "belong in the encounter note — anything written here sits outside the EMR and "
                          + "outside the record the next clinician reads.");

            var visibility = req.Visibility is null ? NoteVisibility.ToFulfiller
                : Enum.TryParse<NoteVisibility>(req.Visibility, out var v) ? v
                : (NoteVisibility?)null;
            if (visibility is null)
                return Results.Problem(statusCode: 422, title: "invalid-visibility",
                    type: "urn:hbmp:invalid-note-visibility");

            var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null) return NotFound();
            var line = order.Lines.FirstOrDefault(l => l.OrderLineId == lineId);
            if (line is null) return NotFound();

            if (await gate.CheckAsync(OrdersPolicies.Read, orderId.ToString(), order.BeneficiaryId,
                    http.Headers.Authorization.ToString(), ct) is { } denied) return denied;

            var reader = ReaderFor(me.Principal, order);
            // A provider may only answer back. Letting them write ToFulfiller or Internal would put words in
            // the ordering clinician's mouth on a surface that reads as clinical instruction.
            if (reader == NoteReader.Fulfiller && visibility != NoteVisibility.FromFulfiller)
                return Results.Problem(statusCode: 403, title: "provider-note-class",
                    type: "urn:hbmp:provider-note-class",
                    detail: "A fulfilling provider writes FromFulfiller notes. Instructions to the fulfiller "
                          + "come from the ordering clinician.");
            if (reader == NoteReader.Other)
                return Results.Problem(statusCode: 403, title: "not-a-note-author",
                    type: "urn:hbmp:not-a-note-author");

            var note = new OrderNote
            {
                NoteId = Guid.NewGuid(), TenantId = order.TenantId,
                SubjectType = "OrderLine", SubjectId = lineId, RootLineId = line.RootLineId,
                Visibility = visibility.Value.ToString(), Body = req.Body.Trim(),
                AuthorUserId = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty,
                AuthorDisplayName = me.Principal?.DisplayName ?? "(not recorded)",
                AuthoredAt = clock.GetUtcNow(), Status = "Active",
            };
            db.OrderNotes.Add(note);
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "order_note", EntityId = note.NoteId.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = note.Visibility, FieldClasses = ["phi"],
            }, ct);

            return Results.Created($"/api/v1/investigation-orders/{orderId}/lines/{lineId}/notes/{note.NoteId}",
                new NoteResponse(note.NoteId, lineId, note.Visibility, note.Body, note.AuthorDisplayName,
                    note.AuthoredAt, note.Status, null, null));
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"))
        .Produces<NoteResponse>();

        // ---- Cancel one: marks, never deletes ----------------------------------------------------------
        v1.MapPost("/notes/{noteId:guid}/cancel", async Task<IResult> (
            Guid noteId, CancelNoteRequest req, OrdersDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.Problem(statusCode: 422, title: "cancel-reason-required",
                    type: "urn:hbmp:cancel-reason-required",
                    detail: "A cancellation keeps the note visible, struck through. \"There was a note here "
                          + "and it was withdrawn, by X, on Y, because Z\" is information; a gap is not.");

            var note = await db.OrderNotes.FirstOrDefaultAsync(n => n.NoteId == noteId, ct);
            if (note is null) return NotFound();
            if (note.Status == "Cancelled")
                return Results.Problem(statusCode: 409, title: "already-cancelled",
                    type: "urn:hbmp:note-already-cancelled");

            note.Status = "Cancelled";
            note.CancelledBy = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty;
            note.CancelledAt = clock.GetUtcNow();
            note.CancelReason = req.Reason.Trim();
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "order_note", EntityId = noteId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Cancelled",
                DecisionReasonCode = req.Reason, FieldClasses = ["phi"],
            }, ct);

            return Results.Ok(new { noteId, status = note.Status, note.CancelledAt, note.CancelReason });
        }).RequireAuthorization(HbmpPolicies.Scope("orders:write"));
    }

    /// <summary>
    /// What this caller is, for note purposes. The EXTERNAL provider is the one this order is routed to —
    /// <c>assigned_provider_id</c>, the row-level ownership anchor design 45 §2b established, and not the
    /// caller's own provider id, which is the R3 defect that made a queue network-wide.
    /// </summary>
    private static NoteReader ReaderFor(HbmpPrincipal? principal, InvestigationOrder order)
    {
        if (principal is null) return NoteReader.Other;

        if (order.AssignedProviderId is { } assigned
            && Guid.TryParse(principal.ProviderId, out var callerProvider)
            && callerProvider != Guid.Empty && callerProvider == assigned)
            return NoteReader.Fulfiller;

        // A caller holding a clinical scope on this service is internal by construction: the gate above has
        // already established a treating relationship or branch scope for this order.
        return principal.ProviderId is null or "" ? NoteReader.InternalClinical
            : NoteReader.Other;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
}
