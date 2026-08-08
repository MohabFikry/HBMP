using System.Reflection;
using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>
/// 14.5 — the booking note.
///
/// <para>The note is a short administrative arrangement shared between reception, the call centre and the
/// treating doctor. It crosses a line the platform otherwise enforces hard — the call centre writes it and a
/// clinician reads it, while the call centre holds no clinical surface anywhere else — so the rules that keep
/// it small and keep it out of the clinical record are worth pinning.</para>
/// </summary>
public class AppointmentNoteTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Blank_becomes_null_rather_than_an_empty_note(string? raw)
    {
        // An empty note and no note are the same fact. Storing "" would paint a note icon on an appointment
        // nobody wrote a note for, and the receptionist who clicks it learns the screen lies.
        AppointmentNote.Normalize(raw).Should().BeNull();
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_but_the_text_is_untouched()
    {
        AppointmentNote.Normalize("  Interpreter needed — Tigrinya  ").Should().Be("Interpreter needed — Tigrinya");
    }

    [Fact]
    public void A_note_within_the_cap_is_accepted()
    {
        var note = AppointmentNote.Normalize("Wheelchair access; ground-floor room if possible.");
        AppointmentNote.Refuse(note).Should().BeNull();
    }

    [Fact]
    public void An_over_long_note_is_REFUSED_not_truncated()
    {
        var note = AppointmentNote.Normalize(new string('x', AppointmentNote.MaxLength + 1));

        var reason = AppointmentNote.Refuse(note);

        // Truncating would silently drop the end of a sentence the operator believes they recorded.
        reason.Should().NotBeNull();
        reason.Should().Contain(AppointmentNote.MaxLength.ToString());
        // And says what the field is FOR, because an operator hitting the cap is usually one about to write
        // something clinical into it.
        reason.Should().Contain("not clinical detail");
    }

    [Fact]
    public void Exactly_the_cap_is_allowed()
    {
        var note = AppointmentNote.Normalize(new string('x', AppointmentNote.MaxLength));
        AppointmentNote.Refuse(note).Should().BeNull();
    }

    /// <summary>
    /// The boundary the note must not cross. It lives on the APPOINTMENT — the scheduling aggregate reception
    /// and the call centre can already read — and must never migrate onto the clinical record, where it would
    /// acquire a clinical audience and a clinical meaning it was never authorised to have.
    /// </summary>
    [Fact]
    public void The_note_is_an_appointment_field_and_not_an_encounter_one()
    {
        typeof(Appointment).GetProperty("Note", BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();

        typeof(Encounter).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().NotContain("Note",
                "the booking note is administrative and shared with the call centre; putting it on the " +
                "encounter would make it part of the clinical record");
    }
}
