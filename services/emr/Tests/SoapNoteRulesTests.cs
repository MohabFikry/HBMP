using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>Sign-lock + author rules for clinical notes (phase-4 guardrail: signed notes are immutable,
/// corrections via addendum, unsigned notes editable by author only).</summary>
public class SoapNoteRulesTests
{
    private static EmrNote Note(string author = "dr-a", bool signed = false) => new()
    {
        NoteId = Guid.NewGuid(), EncounterId = Guid.NewGuid(), NoteType = NoteType.SOAP,
        Assessment = "URTI", AuthoredBy = author, IsSigned = signed,
    };

    [Fact]
    public void Author_can_edit_unsigned_note() =>
        SoapNoteRules.CanEdit(Note(), "dr-a").Should().Be(NoteOutcome.Ok);

    [Fact]
    public void Non_author_cannot_edit() =>
        SoapNoteRules.CanEdit(Note(), "dr-b").Should().Be(NoteOutcome.NotAuthor);

    [Fact]
    public void Signed_note_cannot_be_edited() =>
        SoapNoteRules.CanEdit(Note(signed: true), "dr-a").Should().Be(NoteOutcome.AlreadySigned);

    [Fact]
    public void Author_can_sign_note_with_content() =>
        SoapNoteRules.CanSign(Note(), "dr-a").Should().Be(NoteOutcome.Ok);

    [Fact]
    public void Cannot_sign_twice() =>
        SoapNoteRules.CanSign(Note(signed: true), "dr-a").Should().Be(NoteOutcome.AlreadySigned);

    [Fact]
    public void Cannot_sign_empty_note()
    {
        var empty = new EmrNote { AuthoredBy = "dr-a", NoteType = NoteType.SOAP };
        SoapNoteRules.CanSign(empty, "dr-a").Should().Be(NoteOutcome.EmptyNote);
    }

    [Fact]
    public void Empty_note_has_no_content()
    {
        SoapNoteRules.HasContent(new EmrNote { AuthoredBy = "x" }).Should().BeFalse();
        SoapNoteRules.HasContent(new EmrNote { AuthoredBy = "x", Plan = "rest" }).Should().BeTrue();
    }
}
