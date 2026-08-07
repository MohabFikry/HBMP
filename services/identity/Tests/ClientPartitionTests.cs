using System.Net;
using FluentAssertions;
using Mersal.Identity.Api.Auth;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.1 — who a credential request is attributed to for rate limiting.
///
/// <para>
/// The bug these pin was not a wrong limit. It was the right limit applied to the wrong partition: behind the
/// gateway every caller resolved to the gateway's own address, so ten credential requests a minute were ten
/// for the entire platform. Unauthenticated, from anyone, and nobody can sign in.
/// </para>
/// <para>
/// The arithmetic is one line and both ways of getting it wrong are invisible at runtime — too few hops
/// re-creates the shared bucket, too many hands the client the pen. So both are written down here.
/// </para>
/// </summary>
public class ClientPartitionTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("172.18.0.4");
    private static readonly IPAddress Nginx = IPAddress.Parse("172.18.0.9");
    private const string Client = "203.0.113.7";
    private const string OtherClient = "198.51.100.22";

    private static string Resolve(IPAddress? peer, string? xff, int hops, bool trusted = true) =>
        ClientPartition.Resolve(peer, xff, hops, trusted, out _);

    // ---- the defect itself ----------------------------------------------------------------------------

    [Fact]
    public void Two_clients_behind_the_same_gateway_are_two_partitions()
    {
        // THE regression test. Both requests present the same TCP peer — that is what a gateway is — and the
        // old key was that peer, so these two were one bucket and either one could exhaust it for both.
        var a = Resolve(Gateway, Client, hops: 1);
        var b = Resolve(Gateway, OtherClient, hops: 1);

        a.Should().NotBe(b);
        a.Should().Be(Client);
        b.Should().Be(OtherClient);
    }

    [Fact]
    public void The_gateways_own_address_is_never_the_key_when_a_client_is_recoverable()
    {
        Resolve(Gateway, Client, hops: 1).Should().NotBe(Gateway.ToString());
    }

    // ---- hop arithmetic -------------------------------------------------------------------------------

    [Fact]
    public void One_proxy_takes_the_last_entry()
    {
        // Kong alone: it appends what it saw, which is the client.
        Resolve(Gateway, Client, hops: 1).Should().Be(Client);
    }

    [Fact]
    public void Two_proxies_take_the_second_from_the_right()
    {
        // nginx (so the browser sees one origin) then Kong. nginx appended the client; Kong appended nginx.
        Resolve(Gateway, $"{Client}, {Nginx}", hops: 2).Should().Be(Client);
    }

    [Fact]
    public void A_client_supplied_prefix_is_out_of_reach()
    {
        // The evasion this design exists to refuse. A client that writes its own X-Forwarded-For gets its
        // value carried along in front of the real one — and if it could choose the partition key it could
        // choose a fresh bucket per request, which is no rate limit at all.
        var spoofed = $"9.9.9.9, {Client}, {Nginx}";

        Resolve(Gateway, spoofed, hops: 2).Should().Be(Client);
        Resolve(Gateway, spoofed, hops: 2).Should().NotBe("9.9.9.9");
    }

    [Fact]
    public void Counting_too_far_is_what_hands_the_client_the_pen()
    {
        // Not an endorsement — a demonstration of the cost of a wrong hop count, so that the configuration
        // value has a test explaining why it matters rather than a comment asserting it.
        Resolve(Gateway, $"9.9.9.9, {Client}, {Nginx}", hops: 9).Should().Be("9.9.9.9");
    }

    [Fact]
    public void A_shorter_chain_than_configured_degrades_to_its_leftmost_entry()
    {
        // A service reachable both through the full chain and through a shorter one — a port published for
        // diagnostics, an in-network probe. Clamped rather than refused: refusing would return the shared
        // bucket, which is the outage this whole file is about.
        Resolve(Gateway, Client, hops: 2).Should().Be(Client);
    }

    // ---- trust ----------------------------------------------------------------------------------------

    [Fact]
    public void A_forwarded_header_from_an_untrusted_peer_is_ignored()
    {
        // From a peer we do not vouch for, that header is just something a stranger wrote. The socket is the
        // only evidence in the request.
        var direct = IPAddress.Parse("203.0.113.200");
        ClientPartition.Resolve(direct, "9.9.9.9", trustedHops: 1, peerIsTrustedProxy: false, out _)
            .Should().Be(direct.ToString());
    }

    [Fact]
    public void No_peer_and_no_chain_is_unattributable_and_shares_one_bucket()
    {
        // Unchanged from 18.B3 and deliberately so: "an unattributable caller is exactly the one to keep on a
        // short leash". The alternative — a unique key per unattributable request — is no limit at all for
        // precisely the traffic we can say least about.
        ClientPartition.Resolve(null, null, trustedHops: 1, peerIsTrustedProxy: false, out _)
            .Should().Be(ClientPartition.Unattributable);
    }

    // ---- the misconfiguration signal ------------------------------------------------------------------

    [Fact]
    public void Through_a_proxy_with_no_usable_chain_reports_itself()
    {
        // This IS the pre-28.1 state, and the flag is the only thing that distinguishes it from a quiet
        // morning. A silent fallback here is how a platform-wide sign-in outage comes back after somebody
        // changes a hop count.
        ClientPartition.Resolve(Gateway, "", trustedHops: 1, peerIsTrustedProxy: true, out var misconfigured)
            .Should().Be(Gateway.ToString());
        misconfigured.Should().BeTrue();
    }

    [Fact]
    public void A_recovered_client_is_not_reported_as_misconfigured()
    {
        ClientPartition.Resolve(Gateway, Client, trustedHops: 1, peerIsTrustedProxy: true, out var misconfigured);
        misconfigured.Should().BeFalse();
    }

    [Fact]
    public void A_direct_caller_is_not_reported_as_misconfigured()
    {
        // Nothing is wrong with a request that did not come through a proxy. Warning about it would make the
        // signal above worthless by burying it.
        ClientPartition.Resolve(
            IPAddress.Parse("203.0.113.200"), null, trustedHops: 1, peerIsTrustedProxy: false, out var misconfigured);
        misconfigured.Should().BeFalse();
    }

    // ---- the address family trap ----------------------------------------------------------------------

    [Fact]
    public void An_ipv4_mapped_peer_is_the_same_address_as_its_plain_ipv4_form()
    {
        // THE test that was missing, and its absence let the first version of this fix ship doing nothing.
        //
        // Kestrel listens dual-stack, so a gateway that connected over IPv4 presents as ::ffff:172.18.0.4.
        // IPNetwork.Contains compares address FAMILY first, so every IPv4 CIDR in the trusted list said "not
        // a proxy", the forwarded header was thrown away as a stranger's, and the partition collapsed back
        // onto the gateway — the original outage, with a fix in front of it and green unit tests behind it.
        ClientPartition.Normalise(IPAddress.Parse("::ffff:172.18.0.4"))
            .Should().Be(Gateway);
    }

    [Fact]
    public void A_real_ipv6_address_is_left_alone()
    {
        // Only the mapped form collapses. Rewriting a genuine IPv6 client would merge distinct clients into
        // one bucket, which is the defect this whole file is about, arriving from the other direction.
        var v6 = IPAddress.Parse("2001:db8::1");
        ClientPartition.Normalise(v6).Should().Be(v6);
    }

    [Fact]
    public void One_client_reaching_two_listeners_is_one_bucket()
    {
        // A client must not acquire a second budget by nothing more than which socket accepted it.
        ClientPartition.Normalise(IPAddress.Parse("::ffff:203.0.113.7"))!.ToString()
            .Should().Be(ClientPartition.Normalise(IPAddress.Parse("203.0.113.7"))!.ToString());
    }

    [Fact]
    public void Normalising_nothing_is_nothing()
    {
        ClientPartition.Normalise(null).Should().BeNull();
    }

    // ---- header shapes proxies actually emit ----------------------------------------------------------

    [Fact]
    public void An_ipv4_entry_carrying_a_port_still_parses()
    {
        // Some proxies append host:port. IPAddress.TryParse refuses it, and a parse failure here does not
        // fail loudly — it falls through to the shared bucket, which is the outage wearing a different hat.
        Resolve(Gateway, $"{Client}:51514", hops: 1).Should().Be(Client);
    }

    [Fact]
    public void A_bare_ipv6_entry_is_not_truncated_at_its_first_group()
    {
        // The v4 port rule applied to v6 would cut "2001:db8::1" down to "2001" — every IPv6 client collapsing
        // into one bucket, which is the original defect reproduced for half the internet.
        Resolve(Gateway, "2001:db8::1", hops: 1).Should().Be("2001:db8::1");
    }

    [Fact]
    public void A_bracketed_ipv6_entry_with_a_port_parses()
    {
        Resolve(Gateway, "[2001:db8::1]:443", hops: 1).Should().Be("2001:db8::1");
    }

    [Fact]
    public void Whitespace_and_empty_entries_do_not_shift_the_count()
    {
        // A blank entry that survived the split would move the index by one — the same off-by-one as a wrong
        // hop count, arriving from the data instead of the configuration.
        Resolve(Gateway, $" {Client} , , {Nginx} ", hops: 2).Should().Be(Client);
    }
}
