using System.Text.Json;
using FluentAssertions;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.3 — the minimum-necessary projection over note bodies (design 38 §5.6).
///
/// The hard rule the permission matrix states and this enforces: <b>Finance and the Call Centre never receive
/// a clinical note body.</b> Not "the screen does not render it" — never receive it. So the class-matrix tests
/// below assert over the SERIALIZED payload, because a field present in JSON has already left the building
/// whatever the UI does with it.
///
/// The second half matters as much: a denied caller still sees the note EXISTS. Type, date, author, status.
/// Hiding it entirely would make the member record look empty and send an officer away believing nothing was
/// written, when what they needed was to go ask someone who may read it.
/// </summary>
public class NoteProjectionTests
{
    private static readonly Guid Author = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Reader = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string SecretBody = "Patient disclosed a mental-health history and declined referral.";

    private static Note Note(NoteVisibility visibility, NoteType type = NoteType.General) => new()
    {
        NoteId = Guid.NewGuid(), Scope = NoteScope.Member, ScopeRef = Guid.NewGuid(),
        NoteType = type, Body = SecretBody, VisibilityClass = visibility,
        AuthoredByUserId = Author, AuthoredByUsername = "dr.hoda", AuthoredByDisplay = "Dr Hoda Saleh",
        AuthoredAt = new DateTimeOffset(2026, 3, 4, 9, 30, 0, TimeSpan.Zero),
        Status = NoteStatus.Active,
    };

    private static NoteView Project(Note note, params string[] roles) =>
        NoteView.For(note, roles, Reader, hasSupervisorScope: false);

    /// <summary>Serialize exactly as the API would, then look for the body ANYWHERE in the payload. A field
    /// renamed or nested still fails this — which is the point of asserting over JSON rather than over a
    /// property.</summary>
    private static string Payload(NoteView view) => JsonSerializer.Serialize(view);

    // ---- The hard rule -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("finance")]
    [InlineData("claims_officer")]
    [InlineData("call_center")]
    [InlineData("reception")]
    [InlineData("beneficiary_mgmt")]
    public void A_clinical_body_never_reaches_an_operational_role(string role)
    {
        var view = Project(Note(NoteVisibility.Clinical), role);

        view.Body.Should().BeNull();
        view.BodyWithheld.Should().BeTrue();
        Payload(view).Should().NotContain("mental-health", "the body must not be in the payload at all");
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("nurse")]
    [InlineData("medical_approval")]
    [InlineData("medical_director")]
    [InlineData("case_manager")]
    public void A_clinical_body_reaches_the_clinical_roles(string role)
    {
        var view = Project(Note(NoteVisibility.Clinical), role);

        view.Body.Should().Be(SecretBody);
        view.BodyWithheld.Should().BeFalse();
    }

    [Fact]
    public void A_denied_caller_still_learns_the_note_EXISTS()
    {
        // Existence metadata is the whole reason a withheld note is returned rather than filtered out.
        var note = Note(NoteVisibility.Clinical, NoteType.Exception);

        var view = Project(note, "finance");

        view.NoteType.Should().Be("Exception");
        view.AuthoredByUsername.Should().Be("dr.hoda");
        view.AuthoredByDisplay.Should().Be("Dr Hoda Saleh");
        view.AuthoredAt.Should().Be(note.AuthoredAt);
        view.Status.Should().Be("Active");
        view.WithheldReason.Should().NotBeNullOrWhiteSpace("a blank withheld state reads as a blank note");
    }

    // ---- Restricted: existence-only, released only by grant (design 37 §6) --------------------------------

    [Theory]
    [InlineData("doctor")]
    [InlineData("medical_director")]
    [InlineData("super_admin")]
    public void A_restricted_body_is_withheld_even_from_clinical_roles_without_a_grant(string role)
    {
        // Restricted follows the 37 §6 sensitive pattern: no ROLE grants it. Mental-health, HIV/STI, genetic,
        // substance-use, reproductive and GBV material is released through the request/grant flow or not at all.
        var view = Project(Note(NoteVisibility.Restricted), role);

        view.BodyWithheld.Should().BeTrue();
        view.WithheldReason.Should().Contain("grant");
        Payload(view).Should().NotContain("mental-health");
    }

    [Fact]
    public void A_restricted_body_is_released_to_a_holder_of_an_active_grant()
    {
        var view = NoteView.For(Note(NoteVisibility.Restricted), ["doctor"], Reader,
            hasSupervisorScope: false, hasSensitiveGrant: true);

        view.Body.Should().Be(SecretBody);
        view.BodyWithheld.Should().BeFalse();
    }

    // ---- The author always reads back their own -----------------------------------------------------------

    [Fact]
    public void The_author_always_reads_back_what_they_themselves_wrote()
    {
        // Withholding someone's own signed statement makes the surface unusable for the person most likely to
        // need it — and they already know what it says.
        var view = NoteView.For(Note(NoteVisibility.Restricted), ["finance"], Author, hasSupervisorScope: false);

        view.Body.Should().Be(SecretBody);
        view.BodyWithheld.Should().BeFalse();
    }

    // ---- The other classes --------------------------------------------------------------------------------

    [Fact]
    public void Financial_bodies_reach_the_money_roles_and_not_the_clinical_floor()
    {
        Project(Note(NoteVisibility.Financial), "finance").BodyWithheld.Should().BeFalse();
        Project(Note(NoteVisibility.Financial), "claims_officer").BodyWithheld.Should().BeFalse();
        Project(Note(NoteVisibility.Financial), "nurse").BodyWithheld.Should().BeTrue();
    }

    [Theory]
    [InlineData("finance")]
    [InlineData("call_center")]
    [InlineData("reception")]
    [InlineData("doctor")]
    [InlineData("beneficiary_mgmt")]
    public void Administrative_bodies_reach_everyone_who_works_the_case(string role)
    {
        // The operational record. Restricting this class would make the notes surface pointless.
        Project(Note(NoteVisibility.Administrative), role).BodyWithheld.Should().BeFalse();
    }

    [Fact]
    public void An_unknown_role_receives_no_body_at_any_class_above_administrative()
    {
        // Default-deny. A role nobody thought about must not inherit a clinical body by omission.
        Project(Note(NoteVisibility.Clinical), "some_new_role").BodyWithheld.Should().BeTrue();
        Project(Note(NoteVisibility.Financial), "some_new_role").BodyWithheld.Should().BeTrue();
    }

    // ---- Cancellation authority, projected --------------------------------------------------------------

    [Fact]
    public void Only_the_author_or_a_supervisor_may_cancel()
    {
        var note = Note(NoteVisibility.Administrative);

        NoteView.For(note, ["finance"], Reader, hasSupervisorScope: false).CanCancel.Should().BeFalse();
        NoteView.For(note, ["finance"], Author, hasSupervisorScope: false).CanCancel.Should().BeTrue("the author may");
        NoteView.For(note, ["finance"], Reader, hasSupervisorScope: true).CanCancel.Should().BeTrue("a supervisor may");
    }

    [Fact]
    public void An_already_cancelled_note_cannot_be_cancelled_again()
    {
        var note = Note(NoteVisibility.Administrative);
        note.Status = NoteStatus.Cancelled;

        NoteView.For(note, ["finance"], Author, hasSupervisorScope: true).CanCancel.Should().BeFalse();
    }

    // ---- A cancelled note stays readable ------------------------------------------------------------------

    [Fact]
    public void A_cancelled_note_keeps_its_body_and_shows_who_withdrew_it_and_why()
    {
        // "There was a note here and it was withdrawn, by X, on Y, because Z" is information. A gap is not.
        var note = Note(NoteVisibility.Administrative);
        note.Status = NoteStatus.Cancelled;
        note.CancelledByUsername = "supervisor.amal";
        note.CancelledAt = new DateTimeOffset(2026, 3, 6, 11, 0, 0, TimeSpan.Zero);
        note.CancellationReason = "recorded against the wrong member";

        var view = Project(note, "finance");

        view.Body.Should().Be(SecretBody, "a cancelled note is struck through, not erased");
        view.Status.Should().Be("Cancelled");
        view.CancelledByUsername.Should().Be("supervisor.amal");
        view.CancellationReason.Should().Be("recorded against the wrong member");
    }

    // ---- The signature survives ---------------------------------------------------------------------------

    [Fact]
    public void The_signature_is_a_snapshot_and_survives_the_author_being_renamed()
    {
        // The projection reads the note's OWN columns and never joins to identity, so the signature cannot be
        // rewritten by anything that happens to the author afterwards. This asserts the projection reads the
        // snapshot rather than a live name.
        var note = Note(NoteVisibility.Administrative);

        var view = Project(note, "finance");

        view.AuthoredByUsername.Should().Be("dr.hoda");
        view.AuthoredByDisplay.Should().Be("Dr Hoda Saleh");
    }

    [Fact]
    public void Timestamps_are_returned_in_UTC()
    {
        // The API does not localize. A stored or returned local time is unreadable the moment a second
        // timezone appears; the UI renders Africa/Cairo (design 38 §5.3).
        Project(Note(NoteVisibility.Administrative), "finance").AuthoredAt.Offset.Should().Be(TimeSpan.Zero);
    }

    // ---- Which reads are auditable -------------------------------------------------------------------------

    [Theory]
    [InlineData(NoteVisibility.Clinical, true)]
    [InlineData(NoteVisibility.Restricted, true)]
    [InlineData(NoteVisibility.Financial, false)]
    [InlineData(NoteVisibility.Administrative, false)]
    public void Reading_clinical_or_restricted_material_is_auditable(NoteVisibility visibility, bool expected)
    {
        Note(visibility).ReadIsAuditable.Should().Be(expected);
    }
}
