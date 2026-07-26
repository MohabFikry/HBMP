using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Mersal.Eligibility.Api;

/// <summary>Liveness shared between the <see cref="EventConsumer"/> (writer) and its health check (reader). The
/// consumer keeps the eligibility read models fresh from patient/policy events; if the broker connection drops the
/// service still serves cache/projection reads but its data goes stale, so readiness reports <c>Degraded</c> rather
/// than a hard fail — the pod stays in rotation but the condition is visible to probes and dashboards.</summary>
public sealed class ConsumerHealthState
{
    private volatile bool _connected;
    private long _lastEventTicks;

    public bool Connected
    {
        get => _connected;
        set => _connected = value;
    }

    /// <summary>UTC of the last successfully-applied event, or null if none yet processed this run.</summary>
    public DateTimeOffset? LastEventAt
    {
        get { var t = Interlocked.Read(ref _lastEventTicks); return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero); }
    }

    public void MarkEventApplied() => Interlocked.Exchange(ref _lastEventTicks, DateTimeOffset.UtcNow.UtcTicks);
}

/// <summary>Readiness check for the eligibility event consumer. Healthy when the broker connection is up; Degraded
/// (not Unhealthy) when it is down, because the service can still answer from its projections — the degradation is
/// that those projections stop advancing. Anonymous, cheap, and side-effect-free so probes can poll it freely.</summary>
public sealed class EventConsumerHealthCheck(ConsumerHealthState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["connected"] = state.Connected,
            ["lastEventAt"] = state.LastEventAt?.ToString("O") ?? "none",
        };
        return Task.FromResult(state.Connected
            ? HealthCheckResult.Healthy("event consumer connected", data)
            : HealthCheckResult.Degraded("event consumer not connected — projections may be stale", data: data));
    }
}
