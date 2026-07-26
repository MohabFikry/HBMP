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

/// <summary>A faithful pass-through of an emr response: the HTTP status, the raw body, and any parsed appointment id.</summary>
public sealed record GatewayResult(int StatusCode, string? Body, Guid? AppointmentId)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
