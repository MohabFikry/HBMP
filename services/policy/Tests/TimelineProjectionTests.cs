using System.Text.Json;
using FluentAssertions;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.3c — the timeline projection (design 38 §5c).
///
/// The design rule is that this is a PROJECTION, not a second log. Two properties make that claim真 rather than
/// aspirational, and both are tested here: the projection is DETERMINISTIC (the same source event always
/// produces the same row, id included), and it is IDEMPOTENT (re-projecting cannot duplicate a line in
/// someone's history). Without determinism, "replayable" would mean "produces a similar-looking history with
/// different ids", which no comparison can verify.
/// </summary>
public class TimelineProjectionTests
{
    private static readonly Guid MemberRef = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset When = new(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);

    private static TimelineSource Source(
        string eventType = "MemberEnrolled",
        NoteVisibility visibility = NoteVisibility.Administrative,
        Guid? eventId = null,
        IReadOnlyDictionary<string, (string? Before, string? After)>? changes = null) =>
        new(eventId ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
            eventType, NoteScope.Member, MemberRef, When, "policy-service",
            ActorUserId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ActorUsername: "officer.mona", ActorDisplay: "Mona Adel",
            CorrelationId: "corr-1", VisibilityClass: visibility, Changes: changes);

    private static TimelineEntry Project(TimelineSource s) =>
        TimelineProjection.Project(s, "t0", new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero));

    // ---- Determinism: the property that makes a rebuild verifiable ---------------------------------------

    [Fact]
    public void The_same_source_event_always_produces_the_same_entry_id()
    {
        // Derived from the source event id by hash, not generated. A random id would make a rebuild produce a
        // history that merely LOOKS the same, which no diff can check.
        var id = Guid.NewGuid();

        TimelineProjection.EntryIdFor(id).Should().Be(TimelineProjection.EntryIdFor(id));
    }

    [Fact]
    public void Different_source_events_produce_different_entry_ids()
    {
        TimelineProjection.EntryIdFor(Guid.NewGuid())
            .Should().NotBe(TimelineProjection.EntryIdFor(Guid.NewGuid()));
    }

    [Fact]
    public void Re_projecting_the_same_event_produces_a_byte_identical_row()
    {
        // THE replay guarantee, at the level of the pure function. The projector's idempotency guard and the
        // unique index are the other two layers.
        var source = Source(changes: new Dictionary<string, (string?, string?)>
        {
            ["status"] = ("Active", "Terminated"),
            ["effectiveTo"] = (null, "2026-05-31"),
        });

        var first = Project(source);
        var second = Project(source);

        first.EntryId.Should().Be(second.EntryId);
        first.SummaryEn.Should().Be(second.SummaryEn);
        first.ChangeDiff.Should().Be(second.ChangeDiff, "the diff serializer orders its keys");
        first.OccurredAt.Should().Be(second.OccurredAt);
    }

    [Fact]
    public void The_diff_key_order_is_stable_regardless_of_input_order()
    {
        // Dictionary iteration order is not guaranteed, so without an explicit sort a rebuild could produce
        // the same DATA with different bytes — and "byte-identical" would quietly become untestable.
        var forwards = Project(Source(changes: new Dictionary<string, (string?, string?)>
        {
            ["alpha"] = ("1", "2"), ["beta"] = ("3", "4"), ["gamma"] = ("5", "6"),
        }));
        var backwards = Project(Source(changes: new Dictionary<string, (string?, string?)>
        {
            ["gamma"] = ("5", "6"), ["beta"] = ("3", "4"), ["alpha"] = ("1", "2"),
        }));

        forwards.ChangeDiff.Should().Be(backwards.ChangeDiff);
    }

    // ---- Minimization ------------------------------------------------------------------------------------

    [Fact]
    public void Only_fields_that_actually_changed_reach_the_diff()
    {
        // A diff carrying every field of a row is a COPY of the row, and a timeline of copies is a second
        // database of PHI with none of the controls the first one has.
        var diff = TimelineProjection.Minimize(new Dictionary<string, (string?, string?)>
        {
            ["status"] = ("Active", "Terminated"),
            ["memberNo"] = ("MEM-2026-000001", "MEM-2026-000001"),   // unchanged
            ["groupId"] = ("g-1", "g-1"),                             // unchanged
        });

        diff.Should().Contain("status");
        diff.Should().NotContain("memberNo");
        diff.Should().NotContain("groupId");
    }

    [Fact]
    public void A_change_set_with_nothing_actually_changed_produces_no_diff_at_all()
    {
        // Null rather than "{}" — an empty object renders as an expandable row with nothing in it.
        TimelineProjection.Minimize(new Dictionary<string, (string?, string?)>
        {
            ["status"] = ("Active", "Active"),
        }).Should().BeNull();
    }

    [Fact]
    public void No_changes_means_no_diff()
    {
        TimelineProjection.Minimize(null).Should().BeNull();
        TimelineProjection.Minimize(new Dictionary<string, (string?, string?)>()).Should().BeNull();
    }

    // ---- Class-projected diffs ---------------------------------------------------------------------------

    [Theory]
    [InlineData("finance")]
    [InlineData("claims_officer")]
    [InlineData("call_center")]
    [InlineData("reception")]
    public void A_clinical_diff_is_withheld_from_operational_roles(string role)
    {
        // They see THAT a clinical record changed — actor, timestamp, summary — never WHAT it says.
        var entry = Project(Source(visibility: NoteVisibility.Clinical,
            changes: new Dictionary<string, (string?, string?)> { ["diagnosis"] = ("J06.9", "J45.0") }));

        var view = TimelineEntryView.For(entry, [role]);

        view.ChangeDiff.Should().BeNull();
        view.DiffWithheld.Should().BeTrue();
        JsonSerializer.Serialize(view).Should().NotContain("J45.0", "the values must not be in the payload");
        // …but the entry itself still tells the story.
        view.SummaryEn.Should().NotBeNullOrWhiteSpace();
        view.ActorUsername.Should().Be("officer.mona");
        view.OccurredAt.Should().Be(When);
    }

    [Fact]
    public void A_clinical_diff_reaches_a_clinical_role()
    {
        var entry = Project(Source(visibility: NoteVisibility.Clinical,
            changes: new Dictionary<string, (string?, string?)> { ["diagnosis"] = ("J06.9", "J45.0") }));

        var view = TimelineEntryView.For(entry, ["doctor"]);

        view.ChangeDiff.Should().Contain("J45.0");
        view.DiffWithheld.Should().BeFalse();
    }

    [Fact]
    public void A_withheld_diff_is_distinguishable_from_no_diff()
    {
        // The difference matters in the UI: "details restricted for your role" versus a row that simply had
        // nothing to show. Rendering both as blank tells the reader nothing happened.
        var withNothing = TimelineEntryView.For(Project(Source()), ["finance"]);
        var withheld = TimelineEntryView.For(
            Project(Source(visibility: NoteVisibility.Clinical,
                changes: new Dictionary<string, (string?, string?)> { ["diagnosis"] = ("A", "B") })),
            ["finance"]);

        withNothing.DiffWithheld.Should().BeFalse();
        withheld.DiffWithheld.Should().BeTrue();
    }

    [Fact]
    public void A_restricted_diff_is_withheld_even_from_clinical_roles()
    {
        var entry = Project(Source(visibility: NoteVisibility.Restricted,
            changes: new Dictionary<string, (string?, string?)> { ["note"] = ("x", "y") }));

        TimelineEntryView.For(entry, ["doctor"]).DiffWithheld.Should().BeTrue();
    }

    // ---- Categorization ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("MemberEnrolled", TimelineCategory.Enrolment)]
    [InlineData("MemberTerminated", TimelineCategory.Enrolment)]
    [InlineData("MemberPlanChanged", TimelineCategory.Plan)]
    [InlineData("CoverageGenerated", TimelineCategory.Coverage)]
    [InlineData("NoteAdded", TimelineCategory.Note)]
    [InlineData("NoteCancelled", TimelineCategory.Note)]
    [InlineData("DocumentWithdrawn", TimelineCategory.Document)]
    [InlineData("ClaimDecided", TimelineCategory.Claim)]
    [InlineData("BulkJobCompleted", TimelineCategory.BulkOperation)]
    public void Events_land_in_the_right_category(string eventType, TimelineCategory expected)
    {
        TimelineProjection.CategoryFor(eventType).Should().Be(expected);
    }

    [Theory]
    [InlineData("BreakGlassAccessed")]
    [InlineData("RestrictedDocumentDownloaded")]
    [InlineData("SensitiveNoteRead")]
    public void Access_events_are_part_of_the_story(string eventType)
    {
        // Who VIEWED a restricted document, or used break-glass, belongs on the member's timeline (design 19)
        // — and is frequently the most important line on it.
        TimelineProjection.CategoryFor(eventType).Should().Be(TimelineCategory.Access);
    }

    [Fact]
    public void An_unmapped_event_type_still_produces_an_entry()
    {
        // Dropping it would leave a hole in the history with no trace that anything was missing — the worst
        // possible failure for a record whose purpose is completeness.
        var entry = Project(Source("SomethingNobodyCategorizedYet"));

        entry.EventCategory.Should().Be(TimelineCategory.Administrative);
        entry.SummaryEn.Should().Be("SomethingNobodyCategorizedYet", "the raw type beats an empty summary");
    }

    // ---- Actor snapshot + timestamps ---------------------------------------------------------------------

    [Fact]
    public void The_actor_is_a_snapshot_so_history_survives_a_rename()
    {
        var entry = Project(Source());

        entry.ActorUsername.Should().Be("officer.mona");
        entry.ActorDisplay.Should().Be("Mona Adel");
    }

    [Fact]
    public void Summaries_are_authored_in_both_locales()
    {
        // A timeline an Arabic-speaking officer cannot read is not a timeline. The Arabic is authored, not
        // machine-translated at render time.
        var entry = Project(Source("MemberTerminated"));

        entry.SummaryEn.Should().Be("Membership terminated");
        entry.SummaryAr.Should().Be("تم إنهاء العضوية");
    }

    [Fact]
    public void Timestamps_are_stored_and_returned_in_UTC()
    {
        // Stored UTC, rendered Africa/Cairo by the UI. A stored local time is unreadable the moment a second
        // timezone appears.
        TimelineEntryView.For(Project(Source()), ["finance"]).OccurredAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_entry_carries_its_provenance()
    {
        // source_event_id and correlation_id are what let an entry be traced back to the audit event it came
        // from — the check that the projection has not invented anything.
        var entry = Project(Source());

        entry.SourceEventId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        entry.CorrelationId.Should().Be("corr-1");
        entry.SourceService.Should().Be("policy-service");
    }
}
