using System.Globalization;
using System.Net;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// The one place that answers "which address is this request actually from?" (phase 28.1/28.3).
///
/// <para>
/// Two things need that answer and must not answer it separately: the credential rate limiter, which
/// partitions on it, and the sign-in history, which records it. They were about to be two implementations of
/// the same forwarded-chain walk — which is the shape 27.6 produced a defect in, two call sites one line
/// apart in intent with only one of them looked at. Worse here, because the second copy started out WRONG:
/// it trusted the forwarded header unconditionally, so a direct caller could have written their own address
/// into another person's login history.
/// </para>
/// <para>
/// <see cref="ClientPartition"/> stays a pure function and keeps the arithmetic and its tests. This class is
/// only the configuration and the trust decision around it.
/// </para>
/// </summary>
public sealed class ClientAddressResolver
{
    /// <summary>
    /// Networks whose members are our own proxies, and whose <c>X-Forwarded-For</c> is therefore evidence.
    ///
    /// <para>
    /// Loopback plus the RFC 1918 ranges — what a container network and a k3s pod network both are. A
    /// deliberately different default from <c>ForwardedHeadersOptions.KnownProxies</c>, which is loopback
    /// only: being loopback-only is exactly how the framework silently ignored the header and produced the
    /// shared rate-limit bucket. A default that matches no deployment we have is not a safe default, it is a
    /// disabled feature that reads as an enabled one.
    /// </para>
    /// </summary>
    private const string DefaultTrustedNetworks = "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";

    private readonly int _hops;
    private readonly List<IPNetwork> _trusted = [];

    public ClientAddressResolver(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // How many proxies sit in front. 1 = Kong only; 2 once the SPA's own nginx fronts the gateway so the
        // browser sees one origin. Configuration a deployment STATES, because both ways of getting it wrong
        // are invisible at runtime — see ClientPartition.
        _hops = int.TryParse(config["Forwarding:TrustedHops"], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 1;

        // An unparseable CIDR is skipped rather than fatal. That NARROWS trust, degrading toward the socket
        // address — the conservative direction, and never toward believing a header we should not.
        foreach (var entry in (config["Forwarding:TrustedProxyNetworks"] ?? DefaultTrustedNetworks)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (IPNetwork.TryParse(entry, out var net)) _trusted.Add(net);
    }

    public int TrustedHops => _hops;

    /// <summary>The client's address as a partition key, and whether the deployment looks misconfigured.</summary>
    public string PartitionKey(HttpContext http, out bool misconfigured)
    {
        // Normalised ONCE, and the normalised value is what both the trust check and the key derivation see.
        // Doing it separately in each is how the first version of the 28.1 fix shipped doing nothing.
        var peer = ClientPartition.Normalise(http.Connection.RemoteIpAddress);
        return ClientPartition.Resolve(
            peer,
            http.Request.Headers["X-Forwarded-For"].ToString(),
            _hops,
            peerIsTrustedProxy: IsTrusted(peer),
            out misconfigured);
    }

    /// <summary>The client's address for the record — the sign-in history, and anything else that reports
    /// WHERE a request came from. Falls back to the socket rather than to nothing.</summary>
    public IPAddress? ClientIp(HttpContext http)
    {
        var key = PartitionKey(http, out _);
        return IPAddress.TryParse(key, out var ip)
            ? ip
            : ClientPartition.Normalise(http.Connection.RemoteIpAddress);
    }

    private bool IsTrusted(IPAddress? peer)
    {
        if (peer is null) return false;
        foreach (var net in _trusted)
            if (net.Contains(peer)) return true;
        return false;
    }
}
