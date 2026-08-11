using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>Shared endpoint helpers for the appointment/queue modules: If-Match parsing, branch-scope
/// enforcement, and the TransitionOutcome → RFC 7807 problem mapping.</summary>
internal static class AppointmentEndpointsShared
{
    /// <summary>
    /// The branch an appointment is being booked INTO. A BranchScoped caller (reception, doctor) may only
    /// book into their own active branch, so the body's branchId is never trusted: a mismatch is refused
    /// rather than silently rewritten, because silently moving a booking to another branch is exactly the
    /// surprise design 37 §3 forbids. Branch-unrestricted callers (call centre, member-scoped roles) book
    /// wherever they name.
    ///
    /// <para><b>This used to be the rule itself, and it was wrong for a third of the callers.</b> It asked
    /// <c>ActiveBranchId ==</c>, which predates <see cref="ScopeMode.BranchSetScoped"/> — so a clinics manager
    /// who had not filtered had no active branch, fell straight through the guard, and had the branch id off
    /// their own request body accepted unexamined. It now delegates to <see cref="BranchWriteScope"/>, which
    /// knows all three modes and fails closed in each; the mode rides on <see cref="BranchScopeState.Mode"/>
    /// so no call site has to remember to supply it.</para>
    /// </summary>
    public static (Guid? Branch, IResult? Denied) ResolveBookingBranch(BranchScopeState branch, Guid? requested) =>
        BranchWriteScope.ResolveTarget(branch.Mode, branch.Context, requested);

    /// <summary>Refuse a WRITE against an appointment outside the caller's reach. The read endpoints already
    /// did this; the transitions did not, so a desk in one branch could check in, no-show or cancel another
    /// branch's appointment just by knowing its id. Same delegation, and the same reason, as above.</summary>
    public static async Task<IResult?> DenyIfOutsideBranchAsync(
        Guid appointmentId, BranchScopeState branch, EmrDbContext db, CancellationToken ct)
    {
        // A branch-unrestricted caller is not narrowed by this at all, so do not spend a query on them.
        if (!BranchScopeModes.IsBranchRestricted(branch.Mode)) return null;

        var owning = await db.Appointments.AsNoTracking()
            .Where(a => a.AppointmentId == appointmentId)
            .Select(a => a.BranchId).FirstOrDefaultAsync(ct);

        // A null branch is a pre-branch or external-provider row: BranchWriteScope leaves it to the
        // transition's own 404/409 rather than reporting it as a permission failure.
        return BranchWriteScope.RefuseUnlessWritable(branch.Mode, branch.Context, owning);
    }

    /// <summary>Parse the client's <c>If-Match</c> ETag (the row <c>xmin</c>) into the optimistic-concurrency
    /// token; null when absent or unparseable (endpoint proceeds without the guard).</summary>
    public static uint? IfMatch(HttpRequest http)
    {
        var raw = http.Headers.IfMatch.ToString().Trim().Trim('"');
        return uint.TryParse(raw, out var v) ? v : null;
    }

    public static IResult? MapFailure(TransitionOutcome outcome) => outcome switch
    {
        TransitionOutcome.Ok => null,
        TransitionOutcome.NotFound => Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"),
        TransitionOutcome.IllegalTransition => Results.Problem(statusCode: 409, title: "Transition not allowed",
            type: "urn:hbmp:transition-denied", detail: "The appointment is not in a state that allows this action."),
        TransitionOutcome.SlotTaken => Results.Problem(statusCode: 409, title: "Slot already booked", type: "urn:hbmp:slot-taken"),
        TransitionOutcome.SlotNotFound => Results.Problem(statusCode: 404, title: "Slot not found", type: "urn:hbmp:slot-not-found"),
        TransitionOutcome.PreconditionFailed => Results.Problem(statusCode: 412, title: "Version mismatch",
            type: "urn:hbmp:precondition-failed", detail: "The appointment changed since you last read it; re-fetch and retry."),
        _ => Results.Problem(statusCode: 400),
    };
}
