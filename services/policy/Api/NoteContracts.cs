using Mersal.Policy.Domain;

namespace Mersal.Policy.Api;

// Phase 19.3 request/response contracts for notes (design 38 §5).

/// <summary>Note the ABSENCE of an author field. The signature is taken from the token principal, never from
/// the body — a caller must not be able to sign a note as somebody else.</summary>
public sealed record CreateNote(
    string NoteType, string Body, string VisibilityClass, bool Pinned, Guid? SupersedesNoteId);

public sealed record CancelNote(string Reason);

/// <summary>
/// A note as the CALLER is entitled to see it.
///
/// <para><c>Body</c> is nullable and <c>BodyWithheld</c> is explicit. That pair is the contract: a caller
/// denied a clinical body receives the note's EXISTENCE — type, date, author, status — and is told plainly
/// that a body exists which they may not read. Omitting the note entirely would make the record look empty and
/// send an officer away believing nothing was written; returning an empty string would read as an empty note.</para>
/// </summary>
public sealed record NoteView(
    Guid NoteId,
    string Scope,
    Guid ScopeRef,
    string NoteType,
    string VisibilityClass,
    string? Body,
    bool BodyWithheld,
    string? WithheldReason,
    string AuthoredByUsername,
    string AuthoredByDisplay,
    /// <summary>UTC. The UI renders Africa/Cairo (design 38 §5.3) — the API does not localize timestamps,
    /// because a stored local time is unreadable the moment a second timezone appears.</summary>
    DateTimeOffset AuthoredAt,
    string Status,
    string? CancelledByUsername,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    Guid? SupersedesNoteId,
    bool Pinned,
    /// <summary>Whether THIS caller may cancel it — projected rather than left for the client to infer from
    /// the author id, so the UI's affordance and the API's 403 cannot disagree.</summary>
    bool CanCancel)
{
    /// <summary>Project a note for one caller. The single place a body is either included or withheld.</summary>
    public static NoteView For(
        Note note, IReadOnlyCollection<string> roles, Guid? userId, bool hasSupervisorScope,
        bool hasSensitiveGrant = false)
    {
        ArgumentNullException.ThrowIfNull(note);
        var mayReadBody = NoteVisibilityRules.MayReadBody(
            note.VisibilityClass, roles, userId, note.AuthoredByUserId, hasSensitiveGrant);

        return new NoteView(
            note.NoteId, note.Scope.ToString(), note.ScopeRef, note.NoteType.ToString(),
            note.VisibilityClass.ToString(),
            mayReadBody ? note.Body : null,
            !mayReadBody,
            mayReadBody ? null : note.VisibilityClass == NoteVisibility.Restricted
                ? "Restricted — released only through a report-access grant."
                : $"{note.VisibilityClass} content is not readable by your role.",
            note.AuthoredByUsername, note.AuthoredByDisplay, note.AuthoredAt,
            note.Status.ToString(), note.CancelledByUsername, note.CancelledAt, note.CancellationReason,
            note.SupersedesNoteId, note.Pinned,
            note.MayBeCancelledBy(userId, hasSupervisorScope));
    }
}
