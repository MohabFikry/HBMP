namespace Mersal.Emr.Domain;

/// <summary>The outcome of a note mutation attempt.</summary>
public enum NoteOutcome { Ok, NotAuthor, AlreadySigned, EmptyNote }

/// <summary>Signing/editing rules for clinical notes (phase-4 §4.1, guardrail "signed SOAP notes are
/// immutable — corrections via addendum, never in-place edit"). Pure domain logic; the endpoint applies it.</summary>
public static class SoapNoteRules
{
    /// <summary>A SOAP note must carry at least one populated section to be saved or signed.</summary>
    public static bool HasContent(EmrNote n) =>
        !string.IsNullOrWhiteSpace(n.Subjective) || !string.IsNullOrWhiteSpace(n.Objective)
        || !string.IsNullOrWhiteSpace(n.Assessment) || !string.IsNullOrWhiteSpace(n.Plan);

    /// <summary>May <paramref name="actor"/> edit <paramref name="note"/> in place? Only the author, and only
    /// while unsigned. A signed note is immutable (→ addendum instead).</summary>
    public static NoteOutcome CanEdit(EmrNote note, string actor)
    {
        if (note.IsSigned) return NoteOutcome.AlreadySigned;
        if (!string.Equals(note.AuthoredBy, actor, StringComparison.Ordinal)) return NoteOutcome.NotAuthor;
        return NoteOutcome.Ok;
    }

    /// <summary>May <paramref name="actor"/> sign <paramref name="note"/>? Only the author, only once, and only
    /// when the note has content.</summary>
    public static NoteOutcome CanSign(EmrNote note, string actor)
    {
        if (note.IsSigned) return NoteOutcome.AlreadySigned;
        if (!string.Equals(note.AuthoredBy, actor, StringComparison.Ordinal)) return NoteOutcome.NotAuthor;
        if (!HasContent(note)) return NoteOutcome.EmptyNote;
        return NoteOutcome.Ok;
    }
}
