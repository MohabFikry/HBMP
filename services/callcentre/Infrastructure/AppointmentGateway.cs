namespace Mersal.CallCentre.Infrastructure;

/// <summary>The delegation seam to the emr appointment engine (phase 15.3). The Call Centre REUSES emr's
/// slot-locked, idempotent, If-Match-guarded endpoints — it does not implement a second booking engine. The HTTP
/// implementation forwards the caller's bearer, the Idempotency-Key, and the If-Match ETag verbatim; tests inject a
/// fake. Results are passed through faithfully (status + body) so emr's 409/412/422 semantics reach the agent.</summary>
public interface IAppointmentGateway
{
    Task<GatewayResult> SearchSlotsAsync(string queryString, string? bearer, CancellationToken ct = default);
    Task<GatewayResult> BookAsync(object body, string? bearer, string? idempotencyKey, CancellationToken ct = default);
    Task<GatewayResult> RescheduleAsync(Guid appointmentId, object body, string? bearer, string? idempotencyKey, string? ifMatch, CancellationToken ct = default);
    Task<GatewayResult> CancelAsync(Guid appointmentId, object body, string? bearer, string? idempotencyKey, string? ifMatch, CancellationToken ct = default);
}

/// <summary>A faithful pass-through of an emr response: the HTTP status, the raw body, the body's media type,
/// and any parsed appointment id.
///
/// <para><see cref="ContentType"/> is carried because the pass-through used to relabel every response
/// <c>application/json</c>. A sibling's RFC 7807 error came back as <c>application/problem+json</c> and reached
/// the agent's client claiming to be plain JSON, so a client branching on the media type to render a problem —
/// which is the whole point of the type — could not tell an error from a result. Passing the status through
/// faithfully and the type through falsely is not passing the response through.</para></summary>
public sealed record GatewayResult(int StatusCode, string? Body, Guid? AppointmentId, string? ContentType = null)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The media type to answer with: whatever the sibling actually said, else JSON.</summary>
    public string MediaType => string.IsNullOrWhiteSpace(ContentType) ? "application/json" : ContentType;
}
