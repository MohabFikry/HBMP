using System.Text.Json;

namespace Mersal.Policy.Domain;

// Phase 19.5b — AS-OF reconstruction. "The member list as it stood on 1 March", rebuilt from effective dating
// and enrollment_event rather than read off current rows.
//
// ============================================================================================================
// WHAT "AS OF 1 MARCH" MEANS HERE
// ============================================================================================================
// Two readings are possible and they are not the same:
//
//   (a) THE FACTS AS NOW KNOWN ABOUT 1 MARCH — a member terminated effective 15 March was covered on 1 March,
//       so they appear; a member whose termination was later BACK-DATED to 20 February was not covered, so
//       they do not.
//   (b) WHAT WE BELIEVED ON 1 MARCH — the back-dated member would appear, because on the day itself the
//       correction had not been made.
//
// This implements (a), and says so, because that is what an as-of extract is nearly always FOR: reconciling
// what a payer should have been billed for a period, restating a report after a correction, answering "who was
// covered when this happened". Reading (b) is a question about the organisation's knowledge rather than about
// cover, and it is already answerable — enrollment_event records OccurredAt beside EffectiveDate, and the
// 19.3c timeline replays it. Conflating the two would give one number that is wrong for both questions.

/// <summary>An enrolment event flattened to what as-of reconstruction needs. Parsing the payload here rather
/// than in the query keeps the reconstruction pure and testable without a database.</summary>
public sealed record AsOfEvent(
    EnrollmentEventType Type,
    DateOnly EffectiveDate,
    DateTimeOffset OccurredAt,
    Guid? PolicyPlanId = null,
    Guid? GroupId = null,
    bool CarriesGroup = false)
{
    /// <summary>Read the ids the reconstruction needs out of an event's jsonb payload. Events written before
    /// 19.5b did not carry <c>policyPlanId</c>; the missing value surfaces as an APPROXIMATION flag rather
    /// than as a silently wrong plan label.</summary>
    public static AsOfEvent From(EnrollmentEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Guid? planId = null;
        Guid? groupId = null;
        var carriesGroup = false;

        if (!string.IsNullOrWhiteSpace(e.Payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("policyPlanId", out var p) && p.TryGetGuid(out var pid))
                        planId = pid;
                    // GroupChanged writes {from, to}; Enrolled writes {groupId}. "to": null is a real value —
                    // it means removed from the group — so the presence of the property matters, not just its
                    // content.
                    if (doc.RootElement.TryGetProperty("to", out var to))
                    {
                        carriesGroup = true;
                        if (to.ValueKind == JsonValueKind.String && to.TryGetGuid(out var gid)) groupId = gid;
                    }
                    else if (doc.RootElement.TryGetProperty("groupId", out var g))
                    {
                        carriesGroup = true;
                        if (g.ValueKind == JsonValueKind.String && g.TryGetGuid(out var gid)) groupId = gid;
                    }
                }
            }
            catch (JsonException)
            {
                // A payload we cannot read is treated as carrying nothing. It must not abort the whole
                // extract: one malformed historical row is not a reason to refuse to report on 40 000 members.
            }
        }

        return new AsOfEvent(e.EventType, e.EffectiveDate, e.OccurredAt, planId, groupId, carriesGroup);
    }
}

/// <summary>The membership as it stood on the as-of date.</summary>
public sealed record AsOfMemberState(
    bool WasMember,
    EnrollmentStatus Status,
    Guid? PolicyPlanId,
    Guid? GroupId,
    /// <summary>True when the plan had to be taken from the CURRENT row because no dated event named one —
    /// i.e. the membership predates 19.5b. Surfaced in the extract so a reader can tell a reconstructed value
    /// from an assumed one.</summary>
    bool PlanApproximate,
    bool GroupApproximate);

public static class AsOfMembership
{
    /// <summary>
    /// Rebuild one membership as of <paramref name="asOf"/>.
    ///
    /// <para>Events are applied in EFFECTIVE-date order, with OccurredAt as the tie-break: two changes
    /// effective the same day are applied in the order they were actually decided, so a same-day correction
    /// wins over the thing it corrected.</para>
    /// </summary>
    public static AsOfMemberState Reconstruct(Enrollment enrollment, IEnumerable<AsOfEvent> events, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(events);

        var applicable = events
            .Where(e => e.EffectiveDate <= asOf)
            .OrderBy(e => e.EffectiveDate).ThenBy(e => e.OccurredAt)
            .ToList();

        // A CANCELLED membership is not a membership that ended — it is one that never happened (a
        // mis-keyed enrolment, withdrawn before it took effect). It never appears in an as-of list.
        var wasMember = enrollment.Status != EnrollmentStatus.Cancelled
                        && enrollment.EffectiveFrom <= asOf
                        && (enrollment.EffectiveTo is null || asOf <= enrollment.EffectiveTo.Value);

        var status = EnrollmentStatus.Active;
        Guid? planId = null;
        Guid? groupId = null;
        var planFromEvent = false;
        var groupFromEvent = false;

        foreach (var e in applicable)
        {
            switch (e.Type)
            {
                case EnrollmentEventType.Enrolled:
                    status = EnrollmentStatus.Active;
                    if (e.PolicyPlanId is { } enrolledPlan) { planId = enrolledPlan; planFromEvent = true; }
                    if (e.CarriesGroup) { groupId = e.GroupId; groupFromEvent = true; }
                    break;
                case EnrollmentEventType.PlanChanged:
                    if (e.PolicyPlanId is { } changedPlan) { planId = changedPlan; planFromEvent = true; }
                    break;
                case EnrollmentEventType.GroupChanged:
                    if (e.CarriesGroup) { groupId = e.GroupId; groupFromEvent = true; }
                    break;
                case EnrollmentEventType.Suspended:
                    status = EnrollmentStatus.Suspended;
                    break;
                case EnrollmentEventType.Reinstated:
                    status = EnrollmentStatus.Active;
                    break;
                case EnrollmentEventType.Terminated:
                    status = EnrollmentStatus.Terminated;
                    break;
                case EnrollmentEventType.Corrected:
                default:
                    break;
            }
        }

        // A termination effective ON the as-of date still leaves the member covered that day — the window is
        // inclusive (EnrollmentEntities.cs). So the status says Terminated while WasMember stays true, and both
        // are correct: they were covered on 1 March and 1 March was their last day.
        if (applicable.Count == 0) status = wasMember ? EnrollmentStatus.Active : enrollment.Status;

        return new AsOfMemberState(
            wasMember,
            status,
            planId ?? enrollment.PolicyPlanId,
            groupFromEvent ? groupId : enrollment.GroupId,
            PlanApproximate: !planFromEvent,
            GroupApproximate: !groupFromEvent);
    }
}
