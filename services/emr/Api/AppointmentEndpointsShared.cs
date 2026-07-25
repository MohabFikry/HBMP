using Mersal.Emr.Infrastructure;

namespace Mersal.Emr.Api;

/// <summary>Shared endpoint helpers for the appointment/queue modules: If-Match parsing and the
/// TransitionOutcome → RFC 7807 problem mapping.</summary>
internal static class AppointmentEndpointsShared
{
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
        TransitionOutcome.NotFound => Results.NotFound(),
        TransitionOutcome.IllegalTransition => Results.Problem(statusCode: 409, title: "Transition not allowed",
            type: "urn:hbmp:transition-denied", detail: "The appointment is not in a state that allows this action."),
        TransitionOutcome.SlotTaken => Results.Problem(statusCode: 409, title: "Slot already booked", type: "urn:hbmp:slot-taken"),
        TransitionOutcome.SlotNotFound => Results.Problem(statusCode: 404, title: "Slot not found", type: "urn:hbmp:slot-not-found"),
        TransitionOutcome.PreconditionFailed => Results.Problem(statusCode: 412, title: "Version mismatch",
            type: "urn:hbmp:precondition-failed", detail: "The appointment changed since you last read it; re-fetch and retry."),
        _ => Results.Problem(statusCode: 400),
    };
}
