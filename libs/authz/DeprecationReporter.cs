using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Mersal.Authz;

/// <summary>
/// Reports that a DEPRECATED catalog key is still being resolved — once per (consumer, key), for the life
/// of the process (design 40 §6).
///
/// Deprecation here is a migration signal, not an enforcement one: the key keeps working, because revoking
/// live access the moment someone renames a key is an outage, not a cleanup. What the platform needs
/// instead is EVIDENCE — which consumers still depend on which superseded keys — so umbrella-splits are
/// planned from data rather than from guesswork.
///
/// Once per pair is the whole point. A deprecated key resolves on every single token issuance, so logging
/// each use would emit thousands of identical lines an hour, which is indistinguishable from logging
/// nothing: the signal would be dropped by whoever tuned the noise away. The COUNTER carries the volume;
/// the LOG carries the discovery.
/// </summary>
public sealed class DeprecationReporter(ILogger<DeprecationReporter> log)
{
    /// <summary>The meter name services register with OpenTelemetry to export these counters.</summary>
    public const string MeterName = "Mersal.Authz.Deprecation";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Uses = Meter.CreateCounter<long>(
        "hbmp.authz.deprecated_key_uses",
        unit: "{use}",
        description: "Resolutions of a deprecated catalog key, tagged by consumer and key.");

    private readonly ConcurrentDictionary<(string Consumer, string Key), byte> _seen = new();

    /// <summary>
    /// Record every deprecated key in a computed set. <paramref name="consumer"/> names who resolved it —
    /// the service, or the client id — so the log answers "who do we have to move" and not merely "this key
    /// is still alive somewhere".
    /// </summary>
    public void Report(string consumer, IReadOnlyList<DeprecationUse> uses)
    {
        ArgumentNullException.ThrowIfNull(uses);
        foreach (var use in uses)
        {
            // The counter increments on EVERY use — that is the volume signal, and it is cheap.
            Uses.Add(1, new KeyValuePair<string, object?>("consumer", consumer),
                        new KeyValuePair<string, object?>("key", use.Key));

            // The log fires once per pair. TryAdd returning false means someone already reported it.
            if (!_seen.TryAdd((consumer, use.Key), 0)) continue;

            log.LogWarning(
                "Deprecated authorization key {Key} is still resolved by {Consumer}; migrate to {ReplacedBy}. " +
                "This is logged once per consumer+key for the life of the process — see the " +
                "hbmp.authz.deprecated_key_uses counter for volume.",
                use.Key, consumer, use.ReplacedBy ?? "(no replacement recorded)");
        }
    }
}
