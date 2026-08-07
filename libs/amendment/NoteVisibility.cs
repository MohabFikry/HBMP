namespace Mersal.Amendment;

/// <summary>Who a note is for (design 46 §7b). The class is chosen at WRITE time and the default is
/// <see cref="ToFulfiller"/> — the common case is an instruction meant to be read.</summary>
public enum NoteVisibility
{
    /// <summary>Clinician → the pharmacy / lab / radiology / centre holding the order, plus internal
    /// clinical roles. The widest audience.</summary>
    ToFulfiller,

    /// <summary>Clinician → internal clinical roles ONLY. <b>The external provider never sees this.</b></summary>
    Internal,

    /// <summary>The fulfilling provider → the ordering clinician and internal clinical roles.</summary>
    FromFulfiller,
}

/// <summary>What the caller is, for the purposes of reading notes. Deliberately three cases and not a role
/// list: the question a note projection asks is not "which role" but "are you inside, are you the provider
/// holding this order, or are you neither".</summary>
public enum NoteReader
{
    /// <summary>An internal clinical role — the ordering clinician, a treating colleague, the case team.</summary>
    InternalClinical,

    /// <summary>The external provider this order is routed to.</summary>
    Fulfiller,

    /// <summary>Everyone else. Sees nothing: a note is clinical-adjacent, and a reader who is neither
    /// internal nor the holder of the order has no business in it.</summary>
    Other,
}

/// <summary>
/// 30.5b — which notes a reader may see (design 46 §7b).
///
/// <para><b>The rule that carries the feature</b> is that an external provider never receives an
/// <see cref="NoteVisibility.Internal"/> note. Design 45 §2b built that provider a deliberately narrow
/// projection — no diagnosis, no history, only the clinical context the ordering doctor CHOSE to share — and
/// a free-text note travelling to them unfiltered would be the gap in it. The clinician's internal reasoning
/// is exactly the thing that projection exists to withhold.</para>
///
/// <para>Pure, and shared by both services, because "who can read this" must have ONE answer. Two
/// implementations is the failure design 46 §7b names when it says not to write a fourth notes
/// mechanism.</para>
/// </summary>
public static class NoteAudience
{
    public static bool CanRead(NoteVisibility visibility, NoteReader reader) => reader switch
    {
        // Internal clinical roles see everything: the instruction sent out, the reasoning kept in, and the
        // answer that came back. That is the whole point of them being internal.
        NoteReader.InternalClinical => true,

        // The fulfilling provider sees the instruction meant for them, and what they themselves wrote back.
        // NOT Internal — see the class note.
        NoteReader.Fulfiller => visibility is NoteVisibility.ToFulfiller or NoteVisibility.FromFulfiller,

        _ => false,
    };

    /// <summary>
    /// Filter a set of notes for a reader. Takes the visibility of each rather than a note type, so this
    /// library needs no knowledge of either service's row shape.
    /// </summary>
    public static IEnumerable<T> Readable<T>(
        IEnumerable<T> notes, Func<T, NoteVisibility> visibilityOf, NoteReader reader) =>
        notes.Where(n => CanRead(visibilityOf(n), reader));
}
