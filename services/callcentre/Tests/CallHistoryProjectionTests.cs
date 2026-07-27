using FluentAssertions;
using Mersal.Authz;
using Mersal.CallCentre.Domain;

namespace Mersal.CallCentre.Tests;

/// <summary>
/// Phase 20.3b — the call-history projection and the server-generated clipboard block (design 39 §5b).
///
/// <para>The load-bearing test in this file is <see cref="Narrowing_a_full_row_to_meta_removes_the_summary_from_the_clipboard_too"/>.
/// Everything else checks that the right fields appear at the right level; that one checks the two outputs
/// cannot disagree — which is the failure the whole "generate copyText server-side from the served projection"
/// rule exists to prevent.</para>
/// </summary>
public class CallHistoryProjectionTests
{
    private const string SummaryText =
        "Appointment APT-2026-8841 moved from 25 Jul to 30 Jul at the member's request; member confirmed the new slot.";
    private const string AgentNotes = "caller sounded annoyed, escalate if they ring again";

    private static CallRowSource Source(bool withSummary = true, bool edited = false) => new(
        new CallInteraction
        {
            InteractionId = Guid.NewGuid(),
            CallRef = "CALL-2026-004137",
            TenantId = "t0",
            BeneficiaryId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AgentUserId = Guid.NewGuid(),
            Direction = CallDirection.Outbound,
            StartedAt = new DateTimeOffset(2026, 7, 24, 12, 32, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 7, 24, 12, 38, 12, TimeSpan.Zero),
            ReasonCode = CallReasonCode.RescheduleAppointment,
            Outcome = CallOutcome.Resolved,
            Notes = AgentNotes,
            Summary = withSummary ? SummaryText : null,
            SummaryEditedAt = edited ? DateTimeOffset.UtcNow : null,
            Status = InteractionStatus.Closed,
        },
        "MRS-M-014882",
        "R. Adel",
        "NSR",
        new CallerVerification
        {
            VerificationId = Guid.NewGuid(),
            Result = VerificationResult.Passed,
            VerifiedIdentifierTypes = ["MemberNo", "DateOfBirth"],
        },
        [new LinkedArtifactView("Appointment", "APT-2026-8841", "Reschedule")]);

    // ---------------------------------------------------------------- the levels

    [Fact]
    public void Full_carries_verification_detail_the_agent_and_the_summary()
    {
        var row = CallHistoryProjection.Project(Source(), CallHistoryLevel.Full);

        row.Verification!.Result.Should().Be("Passed");
        row.Verification.IdentifierTypes.Should().BeEquivalentTo(["MemberNo", "DateOfBirth"]);
        row.AgentDisplayName.Should().Be("R. Adel");
        row.Summary.Should().Be(SummaryText);
        row.LinkedArtifacts.Should().ContainSingle();
    }

    [Fact]
    public void Operational_carries_the_summary_but_no_verification_detail_and_no_agent()
    {
        var row = CallHistoryProjection.Project(Source(), CallHistoryLevel.Operational);

        row.Summary.Should().Be(SummaryText, "an approver needs to know what the call was about");
        row.DurationSeconds.Should().Be(372);
        row.BranchCode.Should().Be("NSR");
        row.LinkedArtifacts.Should().ContainSingle();
        // Which identifiers a caller was challenged on is call-centre business, not an approver's.
        row.Verification.Should().BeNull();
        row.AgentDisplayName.Should().BeNull();
    }

    [Fact]
    public void Meta_carries_direction_time_reason_and_outcome_only()
    {
        var row = CallHistoryProjection.Project(Source(), CallHistoryLevel.Meta);

        row.Direction.Should().Be("Outbound");
        row.ReasonCode.Should().Be("RescheduleAppointment");
        row.Outcome.Should().Be("Resolved");
        // Finance sees that a billing call happened, never the narrative (design 39 §5b).
        row.Summary.Should().BeNull();
        row.DurationSeconds.Should().BeNull();
        row.BranchCode.Should().BeNull();
        row.AgentDisplayName.Should().BeNull();
        row.Verification.Should().BeNull();
        row.LinkedArtifacts.Should().BeNull();
    }

    // ---------------------------------------------------------------- agent notes never travel

    [Theory]
    [InlineData(CallHistoryLevel.Meta)]
    [InlineData(CallHistoryLevel.Operational)]
    [InlineData(CallHistoryLevel.Full)]
    public void The_agents_working_notes_never_appear_at_any_level(CallHistoryLevel level)
    {
        // This is why `summary` is a second column rather than a rename of `notes`: widening the audience for
        // call history must not retroactively widen the audience for what an agent typed mid-call.
        var row = CallHistoryProjection.Project(Source(), level);
        var serialized = System.Text.Json.JsonSerializer.Serialize(row);

        serialized.Should().NotContain(AgentNotes);
        row.CopyText.Should().NotContain(AgentNotes);
    }

    // ---------------------------------------------------------------- copyText derivation

    [Fact]
    public void Narrowing_a_full_row_to_meta_removes_the_summary_from_the_clipboard_too()
    {
        // THE test. The clipboard block is built from the SERVED projection, so a level that drops the summary
        // drops it from the copy as well — by construction, not by a second filter that could be forgotten.
        // A Meta viewer with a summary on their clipboard and none in their JSON is the exact leak this
        // prevents, and it would be invisible on screen.
        var full = CallHistoryProjection.Project(Source(), CallHistoryLevel.Full);
        var meta = CallHistoryProjection.Project(Source(), CallHistoryLevel.Meta);

        full.CopyText.Should().Contain(SummaryText);
        meta.CopyText.Should().NotContain(SummaryText);
        meta.CopyText.Should().NotContain("moved from 25 Jul");
    }

    [Fact]
    public void The_clipboard_block_always_carries_provenance()
    {
        // So a pasted summary can be traced back, and cannot be mistaken for a clinical note.
        foreach (var level in new[] { CallHistoryLevel.Meta, CallHistoryLevel.Operational, CallHistoryLevel.Full })
        {
            var row = CallHistoryProjection.Project(Source(), level);
            row.CopyText.Should().Contain("CALL-2026-004137", "the call ref is provenance");
            row.CopyText.Should().Contain("MRS-M-014882", "the member ref is provenance");
            row.CopyText.Should().Contain("Outbound", "direction is stated in words, never colour alone");
            row.CopyText.Should().Contain("2026-07-24", "the timestamp is provenance");
        }
    }

    [Fact]
    public void The_clipboard_block_never_carries_verification_detail()
    {
        // Even at Full, where the row itself shows it. Which identifiers a caller recited is not something that
        // should end up pasted into a ticket or an email.
        var row = CallHistoryProjection.Project(Source(), CallHistoryLevel.Full);
        row.Verification.Should().NotBeNull();
        row.CopyText.Should().NotContain("MemberNo");
        row.CopyText.Should().NotContain("DateOfBirth");
        row.CopyText.Should().NotContain("Passed");
    }

    [Fact]
    public void The_clipboard_block_renders_the_Cairo_time_not_UTC()
    {
        // 12:32 UTC is 15:32 in Cairo on 24 July 2026 — Egypt reintroduced summer time in 2023, so July is
        // UTC+3, not the UTC+2 the design doc's worked example assumed. The offset is taken from the tz
        // database via BusinessCalendar.CairoZone rather than hard-coded, which is why this is right and the
        // illustration in design 39 §5b is a year out of date.
        //
        // The property being defended is that it is CAIRO time at all: an agent reconciling a pasted block
        // against a call log two or three hours off will match it to the wrong call.
        var row = CallHistoryProjection.Project(Source(), CallHistoryLevel.Full);
        var expected = TimeZoneInfo.ConvertTime(
            new DateTimeOffset(2026, 7, 24, 12, 32, 0, TimeSpan.Zero), Mersal.Time.BusinessCalendar.CairoZone);

        row.CopyText.Should().Contain(expected.ToString("yyyy-MM-dd HH:mm",
            System.Globalization.CultureInfo.InvariantCulture));
        row.CopyText.Should().NotContain("12:32", "the block renders Cairo time, never UTC");
    }

    [Fact]
    public void The_clipboard_block_reports_the_duration_in_minutes_and_seconds()
    {
        var row = CallHistoryProjection.Project(Source(), CallHistoryLevel.Full);
        row.CopyText.Should().Contain("(6m 12s)");
    }

    [Fact]
    public void The_direction_word_is_translated_not_transliterated_in_Arabic()
    {
        var row = CallHistoryProjection.Project(Source(), CallHistoryLevel.Full, CallHistoryProjection.Arabic);
        row.CopyText.Should().Contain("صادر");
        row.CopyText.Should().Contain("العضو");
        row.CopyText.Should().NotContain("Member:");
    }

    [Fact]
    public void Copy_all_joins_the_blocks_that_were_actually_served()
    {
        var rows = new[]
        {
            CallHistoryProjection.Project(Source(), CallHistoryLevel.Meta),
            CallHistoryProjection.Project(Source(), CallHistoryLevel.Meta),
        };

        var joined = CallHistoryProjection.CopyAll(rows);
        joined.Should().NotContain(SummaryText, "copy-all is the same projection, not a wider one");
        joined.Split("\n\n").Should().HaveCount(2);
    }

    // ---------------------------------------------------------------- the edited marker

    [Fact]
    public void An_edited_summary_is_marked_as_edited()
    {
        // A summary other roles rely on that can be rewritten without trace still reads as a record — which is
        // worse than no summary at all.
        CallHistoryProjection.Project(Source(edited: true), CallHistoryLevel.Full)
            .SummaryEdited.Should().BeTrue();
        CallHistoryProjection.Project(Source(), CallHistoryLevel.Full)
            .SummaryEdited.Should().BeFalse();
    }

    // ---------------------------------------------------------------- the required-summary rule

    [Theory]
    [InlineData(CallOutcome.Resolved, true)]
    [InlineData(CallOutcome.FollowUpRequired, true)]
    [InlineData(CallOutcome.Transferred, true)]
    [InlineData(CallOutcome.NoAction, true)]
    [InlineData(CallOutcome.Abandoned, false)]
    public void A_summary_is_required_at_close_for_every_outcome_but_abandoned(CallOutcome outcome, bool required)
    {
        // Abandoned is excluded because there is nothing to account for — and demanding one would train agents
        // to type "abandoned" into the field other roles read.
        CallSummaryRules.IsRequiredAtClose(outcome).Should().Be(required);
        (CallSummaryRules.Validate(outcome, null) is not null).Should().Be(required);
    }

    [Fact]
    public void A_summary_over_the_cap_is_rejected_rather_than_truncated()
    {
        // Truncating would silently change what a coordinator reads, mid-sentence.
        var tooLong = new string('x', CallSummaryRules.MaxLength + 1);
        CallSummaryRules.Validate(CallOutcome.Resolved, tooLong).Should().Contain("capped at 500");
        CallSummaryRules.Validate(CallOutcome.Resolved, new string('x', CallSummaryRules.MaxLength))
            .Should().BeNull();
    }

    [Fact]
    public void A_whitespace_only_summary_does_not_satisfy_the_rule() =>
        CallSummaryRules.Validate(CallOutcome.Resolved, "   ").Should().NotBeNull();

    // ---------------------------------------------------------------- level clamping

    [Fact]
    public void A_client_supplied_level_may_narrow_but_never_widen()
    {
        // The clamp belongs on the server. Rejecting a narrowing request would be obstructive; honouring a
        // widening one would be the bug.
        ProfilePolicies.Clamp(CallHistoryLevel.Full, CallHistoryLevel.Meta).Should().Be(CallHistoryLevel.Meta);
        ProfilePolicies.Clamp(CallHistoryLevel.Meta, CallHistoryLevel.Full).Should().Be(CallHistoryLevel.Meta);
    }

    [Fact]
    public void A_row_with_no_summary_recorded_still_produces_a_usable_block()
    {
        var row = CallHistoryProjection.Project(Source(withSummary: false), CallHistoryLevel.Full);
        row.Summary.Should().BeNull();
        row.CopyText.Should().Contain("CALL-2026-004137");
        row.CopyText.Split('\n').Should().HaveCount(3, "provenance lines only, no empty summary line");
    }
}
