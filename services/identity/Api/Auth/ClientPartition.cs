using System.Net;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Who a credential request is FROM, for the purpose of rate-limiting it (ADR-0036 §9, phase 28.1).
///
/// <para>
/// ============================================================================================================
/// THE DEFECT THIS EXISTS TO FIX
/// ============================================================================================================
/// <c>IssuerRateLimits</c> partitions on the client IP so that "one abusive source cannot deny service to the
/// rest of the tenant". It was partitioning on <see cref="ConnectionInfo.RemoteIpAddress"/>, which behind the
/// gateway is <b>the gateway</b> — one address for every caller on the platform. So the partition was a single
/// shared bucket of ten credential requests per minute, for everybody.
/// </para>
/// <para>
/// That is not a weak limit. It is a <b>pre-authentication denial of service on signing in</b>: ten HTTP
/// requests a minute, from anyone, with no account and no credentials, and nobody in any clinic can log in.
/// Proven live before this was written — twelve POSTs to <c>/connect/login</c> through Kong from the host gave
/// ten 200s and two 429s, a request from a completely different source IP immediately afterwards got 429, and
/// the same source got 200 once the window had elapsed.
/// </para>
/// <para>
/// It was reachable by two different routes, which is why reading either one alone would have missed it.
/// In Development <c>UseHbmpTransportSecurity</c> returns before <c>UseForwardedHeaders</c> ever runs, so no
/// forwarded header is honoured at all. Outside Development it runs, but
/// <c>ForwardedHeadersOptions.KnownProxies</c> defaults to loopback — a gateway on a container network is not
/// loopback, so the header is <b>silently ignored</b> and the outcome is identical.
/// </para>
///
/// <para>
/// ============================================================================================================
/// WHY THIS IS A PURE FUNCTION
/// ============================================================================================================
/// The whole thing is hop arithmetic, and hop arithmetic is exactly what gets quietly wrong: one hop too few
/// and a proxy's own address becomes the partition key (the shared bucket, back again); one hop too many and a
/// value the CLIENT wrote becomes the key, which lets an attacker evade their own limit by rotating a header.
/// Neither failure has a symptom you would notice — both look like a rate limiter that is working.
/// </para>
/// <para>
/// So the arithmetic is separated from the server and tested directly, with the spoofing case written down as
/// a test rather than as a claim in a comment.
/// </para>
/// </summary>
public static class ClientPartition
{
    /// <summary>
    /// The bucket every unattributable caller shares.
    ///
    /// <para>
    /// Kept deliberately, and kept SHARED. The original file's reasoning holds — "an unattributable caller is
    /// exactly the one to keep on a short leash" — and the alternative, a per-request key, would mean no limit
    /// at all for precisely the traffic we can say least about.
    /// </para>
    /// <para>
    /// What changes is that arriving here <i>through a proxy</i> is now reported (see
    /// <see cref="Resolve"/>'s <c>misconfigured</c> flag). A silent fallback to this bucket is how the
    /// platform-wide outage above would come back after someone changed a hop count, and it would look exactly
    /// like a busy morning.
    /// </para>
    /// </summary>
    public const string Unattributable = "unknown-client";

    /// <summary>
    /// Collapse an IPv4-mapped IPv6 address to plain IPv4.
    ///
    /// <para>
    /// <b>This is not tidying. Skipping it is how the first version of this fix silently did nothing.</b>
    /// Kestrel listens dual-stack, so a peer that connected over IPv4 arrives as <c>::ffff:172.18.0.4</c> —
    /// family <c>InterNetworkV6</c>. <see cref="IPNetwork.Contains"/> compares address families first and
    /// returns <c>false</c> across them, so every IPv4 CIDR in the trusted list rejected the gateway, the
    /// forwarded header was discarded as untrusted, and the partition fell back to the peer: the exact shared
    /// bucket this file exists to remove. Diagnosed live — the header was present and correct
    /// (<c>xff='172.18.0.1'</c>) while the key was still the gateway.
    /// </para>
    /// <para>
    /// It also keeps ONE bucket per client. Without it the same address reaching a dual-stack listener and an
    /// IPv4 one would key differently, so a client would get two budgets by changing nothing.
    /// </para>
    /// </summary>
    public static IPAddress? Normalise(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>
    /// Resolve the partition key for one request.
    /// </summary>
    /// <param name="peer">The immediate TCP peer — the gateway, when there is one.</param>
    /// <param name="forwardedFor">The raw <c>X-Forwarded-For</c> header, if any.</param>
    /// <param name="trustedHops">
    /// How many proxies sit in front of this service. <b>Exactly</b> how many: each appends the address it saw,
    /// so the client is the entry <c>trustedHops</c> from the right. One nginx in front of Kong is 2.
    /// </param>
    /// <param name="peerIsTrustedProxy">
    /// Whether <paramref name="peer"/> is one of our own proxies. A forwarded header from anyone else is a
    /// header a stranger wrote, and is ignored — that is the only thing standing between this and an attacker
    /// choosing their own rate-limit bucket per request.
    /// </param>
    /// <param name="misconfigured">
    /// True when the peer IS a trusted proxy and yet no usable client address could be recovered. The caller
    /// logs this. It means the hop count and the deployment disagree, and the consequence is the shared bucket.
    /// </param>
    public static string Resolve(
        IPAddress? peer, string? forwardedFor, int trustedHops, bool peerIsTrustedProxy, out bool misconfigured)
    {
        misconfigured = false;

        // No proxy in front (a direct call, or a peer we do not vouch for): the socket is the best evidence
        // there is, and any forwarded header on such a request is unverifiable — the client could have written
        // it themselves. Ignored rather than preferred.
        if (!peerIsTrustedProxy)
            return peer?.ToString() ?? Unattributable;

        var chain = Split(forwardedFor);
        if (chain.Count > 0)
        {
            // Each proxy APPENDS what it saw, so the chain reads [ ...anything the client sent..., client,
            // proxy1, proxy2 ]. Counting `trustedHops` from the right lands on the client and stops there,
            // which is what leaves a client-supplied prefix unreachable.
            //
            // Clamped at 0 rather than rejected. A service reachable both through the full chain and through a
            // shorter one — a gateway published directly for diagnostics, a health probe inside the network —
            // sees a shorter chain than configured, and the honest reading of a short chain is its leftmost
            // entry. Clamping degrades to "the earliest address anyone recorded"; rejecting would degrade to
            // the shared bucket, which is the outage.
            var index = Math.Max(0, chain.Count - Math.Max(1, trustedHops));
            if (IPAddress.TryParse(chain[index], out var client))
                return client.ToString();
        }

        // Through a proxy, and still nothing to attribute this to. Not silent.
        misconfigured = true;
        return peer?.ToString() ?? Unattributable;
    }

    /// <summary>
    /// Split an <c>X-Forwarded-For</c> value, dropping the empties.
    ///
    /// <para>
    /// Ports are stripped from IPv4 entries (<c>203.0.113.4:51514</c>) because some proxies add them and
    /// <see cref="IPAddress.TryParse"/> refuses them — and a parse failure here does not fail loudly, it
    /// falls through to the shared bucket. IPv6 entries are left alone: they are bracketed when they carry a
    /// port (<c>[2001:db8::1]:443</c>) and full of colons when they do not, so the same rule applied to both
    /// would truncate every bare IPv6 address at its first group.
    /// </para>
    /// </summary>
    private static List<string> Split(string? header)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(header)) return parts;

        foreach (var raw in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw;
            if (entry.StartsWith('[') && entry.Contains("]:", StringComparison.Ordinal))
                entry = entry[1..entry.IndexOf("]:", StringComparison.Ordinal)];        // [v6]:port
            else if (entry.StartsWith('[') && entry.EndsWith(']'))
                entry = entry[1..^1];                                                    // [v6]
            else if (entry.Count(c => c == ':') == 1)
                entry = entry[..entry.IndexOf(':')];                                     // v4:port

            if (entry.Length > 0) parts.Add(entry);
        }
        return parts;
    }
}
