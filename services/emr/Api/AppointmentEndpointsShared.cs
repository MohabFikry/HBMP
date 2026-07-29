using Mersal.Authz;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Api;

/// <summary>Shared endpoint helpers for the appointment/queue modules: If-Match parsing, branch-scope
/// enforcement, and the TransitionOutcome → RFC 7807 problem mapping.</summary>
internal static class AppointmentEndpointsShared
{
    /// <summary>The branch an appointment is being booked INTO. A BranchScoped caller (reception, doctor) may
    /// only book into their own active branch, so the body's branchId is never trusted: a mismatch is refused
    /// rather than silently rewritten, because silently moving a booking to another branch is exactly the
    /// surprise design 37 §3 forbids. Branch-unrestricted callers (call centre, member-scoped roles) book
    /// wherever they name.</summary>
    public static (Guid? Branch, IResult? Denied) ResolveBookingBranch(BranchScopeState branch, Guid? requested)
    {
        if (branch.Context.ActiveBranchId is not { } active) return (requested, null);
        if (requested is { } r && r != active)
            return (null, Results.Problem(statusCode: 403, title: "branch-scope-denied",
                type: "urn:hbmp:branch-scope-denied",
                detail: "you can only book into your active branch"));
        return (active, null);
    }

    /// <summary>Refuse a WRITE against an appointment outside the caller's active branch. The read endpoints
    /// already did this; the transitions did not, so a desk in one branch could check in, no-show or cancel
    /// another branch's appointment just by knowing its id.</summary>
    public static async Task<IResult?> DenyIfOutsideBranchAsync(
        Guid appointmentId, BranchScopeState branch, EmrDbContext db, CancellationToken ct)
    {
        if (branch.Context.ActiveBranchId is not { } active) return null;
        var owning = await db.Appointments.AsNoTracking()
            .Where(a => a.AppointmentId == appointmentId)
            .Select(a => a.BranchId).FirstOrDefaultAsync(ct);
        // A null branch is a pre-branch or external-provider row: leave it to the transition's own 404/409.
        if (owning is null || owning == active) return null;
        return Results.Problem(statusCode: 403, title: "branch-scope-denied",
            type: "urn:hbmp:branch-scope-denied",
            detail: "this appointment is not in your active branch");
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
