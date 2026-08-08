using FluentAssertions;

namespace Mersal.Amendment.Tests;

/// <summary>
/// 30.5b — design 46 §7b's three visibility classes, and the one that carries the feature.
///
/// <para>An external centre seeing a clinician's internal reasoning would widen the deliberately narrow
/// projection design 45 §2b built for them — no diagnosis, no history, only the context the ordering doctor
/// CHOSE to share. A note travelling unfiltered is the gap in that gate, and it is the kind of gap nobody
/// notices, because the note renders correctly and the screen looks right.</para>
/// </summary>
public class NoteAudienceTests
{
    [Fact]
    public void The_external_provider_NEVER_sees_an_Internal_note()
    {
        NoteAudience.CanRead(NoteVisibility.Internal, NoteReader.Fulfiller).Should().BeFalse(
            "a clinician's internal reasoning is exactly what the provider projection exists to withhold");
    }

    [Fact]
    public void The_external_provider_sees_the_instruction_meant_for_them_and_their_own_reply()
    {
        NoteAudience.CanRead(NoteVisibility.ToFulfiller, NoteReader.Fulfiller).Should().BeTrue(
            "'fasting sample' is worthless if the lab cannot read it");
        NoteAudience.CanRead(NoteVisibility.FromFulfiller, NoteReader.Fulfiller).Should().BeTrue(
            "they wrote it");
    }

    [Theory]
    [InlineData(NoteVisibility.ToFulfiller)]
    [InlineData(NoteVisibility.Internal)]
    [InlineData(NoteVisibility.FromFulfiller)]
    public void Internal_clinical_roles_see_every_class(NoteVisibility visibility) =>
        NoteAudience.CanRead(visibility, NoteReader.InternalClinical).Should().BeTrue();

    [Theory]
    [InlineData(NoteVisibility.ToFulfiller)]
    [InlineData(NoteVisibility.Internal)]
    [InlineData(NoteVisibility.FromFulfiller)]
    public void Everyone_else_sees_nothing(NoteVisibility visibility) =>
        NoteAudience.CanRead(visibility, NoteReader.Other).Should().BeFalse(
            "a reader who is neither internal nor the holder of this order has no business in its notes");

    [Fact]
    public void Filtering_a_mixed_set_for_a_provider_drops_only_the_internal_ones()
    {
        var notes = new[]
        {
            (Id: 1, V: NoteVisibility.ToFulfiller),
            (Id: 2, V: NoteVisibility.Internal),
            (Id: 3, V: NoteVisibility.FromFulfiller),
            (Id: 4, V: NoteVisibility.Internal),
        };

        NoteAudience.Readable(notes, n => n.V, NoteReader.Fulfiller).Select(n => n.Id)
            .Should().Equal(1, 3);
        NoteAudience.Readable(notes, n => n.V, NoteReader.InternalClinical).Select(n => n.Id)
            .Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void An_unrecognised_reader_defaults_to_seeing_nothing()
    {
        // Fail closed. A new reader kind added without touching this rule must default to the narrow answer,
        // not the wide one — the direction of the mistake is what matters on a disclosure gate.
        NoteAudience.CanRead(NoteVisibility.ToFulfiller, (NoteReader)99).Should().BeFalse();
    }
}
