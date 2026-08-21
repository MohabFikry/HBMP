using Mersal.Amendment;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Api;

/// <summary>Write a note on a prescription line. <paramref name="Visibility"/> defaults to
/// <c>ToFulfiller</c> — the common case is an instruction meant to be read at the counter.</summary>
public sealed record WriteRxNoteRequest(string Body, string? Visibility);

public sealed record CancelRxNoteRequest(string Reason);

/// <summary>
/// What a reader receives. <b>A note they may not read is not returned at all</b>, so the shape carries no
/// trace of it.
/// </summary>
public sealed record RxNoteResponse(
    Guid NoteId, Guid PrescriptionLineId, string Visibility, string Body,
    string AuthorDisplayName, DateTimeOffset AuthoredAt, string Status,
    DateTimeOffset? CancelledAt, string? CancelReason);

/// <summary>
/// 32.5 — notes on a prescription line (design 46 §7b).
///
/// <para>The PORT of <c>orders/Api/Notes.cs</c>, not a second implementation. Doc 46 §7b is titled "Notes on
/// prescriptions, labs, radiology and procedures" and orders-service built all of it except the first word:
/// a prescription line was the one order kind with nowhere to put "patient cannot swallow tablets — syrup if
/// available". The doc's own reason for demanding reuse is the one that governs this file — "a second notes
/// mechanism means two behaviours for 'cancel a note' and two answers to 'who can read this'" — so the
/// vocabulary is <c>libs/amendment</c>'s and the rules below are orders' rules, not new ones.</para>
///
/// <para><b>Adding a note is not an amendment.</b> Nothing here touches <c>prescription_line</c>: no
/// supersede, no version, no authorisation change. Conflating the two would send every "take with food" back
/// to the approval queue.</para>
/// </summary>
public static class RxNoteEndpoints
{
    public static void MapPrescriptionNotes(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1/prescriptions").RequireAuthorization();

        // ---- Read the notes on a line ------------------------------------------------------------------
        v1.MapGet("/{rxId:guid}/lines/{lineId:guid}/notes", async Task<IResult> (
            Guid rxId, Guid lineId, HttpRequest http, PharmacyDbContext db, PharmacyGate gate,
            DispensingGate dispensing, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var (rx, line, missing) = await FindAsync(db, rxId, lineId, ct);
            if (missing is not null) return missing;

            var reader = ReaderFor(me.Principal);
            if (await AuthorizeAsync(reader, gate, dispensing, rxId, rx!.BeneficiaryId, http, ct) is { } denied)
                return denied;

            // The chain, not the row: a note written on v1 is about the clinical intent, which survives the
            // amendment, so it stays visible on v2.
            var notes = await db.PrescriptionNotes.AsNoTracking()
                .Where(n => n.RootLineId == line!.RootLineId)
                .OrderByDescending(n => n.AuthoredAt).ToListAsync(ct);

            // THE PROJECTION. Filtered before serialization, so a note the caller may not read never reaches
            // the payload at all — "the screen does not show it" is not a control.
            var visible = NoteAudience
                .Readable(notes, n => Enum.Parse<NoteVisibility>(n.Visibility), reader)
                .Select(n => new RxNoteResponse(
                    n.NoteId, n.SubjectId, n.Visibility, n.Body, n.AuthorDisplayName, n.AuthoredAt,
                    n.Status, n.CancelledAt, n.CancelReason))
                .ToList();

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription_note", EntityId = lineId.ToString(), Action = AuditAction.Read,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Allow",
                DecisionReasonCode = $"reader:{reader};notes:{visible.Count}", FieldClasses = ["phi"],
            }, ct);

            return Results.Ok(visible);
        }).RequireAuthorization(HbmpPolicies.AnyScope("rx:read", "pharmacy:read"))
        .Produces<IEnumerable<RxNoteResponse>>();

        // ---- Write one --------------------------------------------------------------------------------
        v1.MapPost("/{rxId:guid}/lines/{lineId:guid}/notes", async Task<IResult> (
            Guid rxId, Guid lineId, WriteRxNoteRequest req, HttpRequest http, PharmacyDbContext db,
            PharmacyGate gate, DispensingGate dispensing, IAuditClient audit, IHbmpPrincipalAccessor me,
            TimeProvider clock, CancellationToken ct) =>
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

            var (rx, line, missing) = await FindAsync(db, rxId, lineId, ct);
            if (missing is not null) return missing;

            var reader = ReaderFor(me.Principal);
            if (await AuthorizeAsync(reader, gate, dispensing, rxId, rx!.BeneficiaryId, http, ct) is { } denied)
                return denied;

            // A counter may only answer back. Letting it write ToFulfiller or Internal would put words in the
            // prescriber's mouth on a surface that reads as clinical instruction.
            if (reader == NoteReader.Fulfiller && visibility != NoteVisibility.FromFulfiller)
                return Results.Problem(statusCode: 403, title: "provider-note-class",
                    type: "urn:hbmp:provider-note-class",
                    detail: "A dispensing pharmacy writes FromFulfiller notes. Instructions to the counter "
                          + "come from the prescriber.");
            if (reader == NoteReader.Other)
                return Results.Problem(statusCode: 403, title: "not-a-note-author",
                    type: "urn:hbmp:not-a-note-author");

            var note = new PrescriptionNote
            {
                NoteId = Guid.NewGuid(), TenantId = rx.TenantId,
                SubjectType = "PrescriptionLine", SubjectId = lineId, RootLineId = line!.RootLineId,
                Visibility = visibility.Value.ToString(), Body = req.Body.Trim(),
                AuthorUserId = Guid.TryParse(me.Principal?.Subject, out var a) ? a : Guid.Empty,
                AuthorDisplayName = me.Principal?.DisplayName ?? "(not recorded)",
                AuthoredAt = clock.GetUtcNow(), Status = "Active",
            };
            db.PrescriptionNotes.Add(note);
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "prescription_note", EntityId = note.NoteId.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = note.Visibility, FieldClasses = ["phi"],
            }, ct);

            return Results.Created($"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes/{note.NoteId}",
                new RxNoteResponse(note.NoteId, lineId, note.Visibility, note.Body, note.AuthorDisplayName,
                    note.AuthoredAt, note.Status, null, null));
        }).RequireAuthorization(HbmpPolicies.AnyScope("rx:write", "pharmacy:dispense"))
        .Produces<RxNoteResponse>();

        // ---- Cancel one: marks, never deletes ----------------------------------------------------------
        v1.MapPost("/notes/{noteId:guid}/cancel", async Task<IResult> (
            Guid noteId, CancelRxNoteRequest req, PharmacyDbContext db, IAuditClient audit,
            IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.Problem(statusCode: 422, title: "cancel-reason-required",
                    type: "urn:hbmp:cancel-reason-required",
                    detail: "A cancellation keeps the note visible, struck through. \"There was a note here "
                          + "and it was withdrawn, by X, on Y, because Z\" is information; a gap is not.");

            var note = await db.PrescriptionNotes.FirstOrDefaultAsync(n => n.NoteId == noteId, ct);
            if (note is null) return Results.Problem(statusCode: 404, title: "Not Found",
                type: "https://mersal.foundation/problems/not-found");
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
                EntityType = "prescription_note", EntityId = noteId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = me.Principal?.Subject, DecisionOutcome = "Cancelled",
                DecisionReasonCode = req.Reason, FieldClasses = ["phi"],
            }, ct);

            return Results.Ok(new { noteId, status = note.Status, note.CancelledAt, note.CancelReason });
        }).RequireAuthorization(HbmpPolicies.AnyScope("rx:write", "pharmacy:dispense"));
    }

    /// <summary>
    /// Ask the gate that matches what the caller IS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One endpoint, two legitimate relationships, and pharmacy already encodes them separately for good
    /// reason. <see cref="PharmacyGate"/> asks whether the caller TREATS this beneficiary — the clinician's
    /// link. <see cref="DispensingGate"/> asks whether they are a dispensing pharmacy — the counter's. A
    /// pharmacist does not treat anybody, so putting the clinician's gate in front of a note the counter is
    /// meant to read refuses the reader the note exists for; that is what the first run of this suite did.
    /// </para>
    /// <para>
    /// Not a widening: each caller still passes the check that was written for them. What changes is that
    /// the endpoint stops assuming there is only one kind of caller.
    /// </para>
    /// </remarks>
    private static async Task<IResult?> AuthorizeAsync(
        NoteReader reader, PharmacyGate gate, DispensingGate dispensing,
        Guid rxId, Guid beneficiaryId, HttpRequest http, CancellationToken ct)
        => reader == NoteReader.Fulfiller
            ? await dispensing.AuthorizeSearchAsync(ct)
            : await gate.CheckAsync("rx:read", "prescription", rxId.ToString(), beneficiaryId,
                http.Headers.Authorization.ToString(), ct);

    private static async Task<(Prescription? Rx, PrescriptionLine? Line, IResult? Missing)> FindAsync(
        PharmacyDbContext db, Guid rxId, Guid lineId, CancellationToken ct)
    {
        var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.PrescriptionId == rxId, ct);
        var line = rx?.Lines.FirstOrDefault(l => l.PrescriptionLineId == lineId);
        if (rx is null || line is null)
            return (null, null, Results.Problem(statusCode: 404, title: "Not Found",
                type: "https://mersal.foundation/problems/not-found"));
        return (rx, line, null);
    }

    /// <summary>
    /// What this caller is, for note purposes.
    /// </summary>
    /// <remarks>
    /// SIMPLER THAN ORDERS', and the difference is real rather than an omission. An investigation order is
    /// routed to one provider, so orders compares the caller against <c>assigned_provider_id</c> — the
    /// row-level ownership anchor. A prescription is not routed anywhere: any pharmacy in the network may
    /// dispense it, which is why <c>DispensingGate</c> asks only whether the caller HAS a dispensing
    /// pharmacy. So a caller carrying a provider claim is the fulfiller here, and one carrying none — having
    /// already passed a gate that established a treating relationship — is internal.
    /// </remarks>
    private static NoteReader ReaderFor(HbmpPrincipal? principal)
    {
        if (principal is null) return NoteReader.Other;
        return string.IsNullOrWhiteSpace(principal.ProviderId)
            ? NoteReader.InternalClinical
            : NoteReader.Fulfiller;
    }
}
