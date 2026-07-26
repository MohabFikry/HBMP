using System.Text.Json.Nodes;
using FluentAssertions;
using Mersal.Interop.Domain.Integration;

namespace Mersal.Interop.Tests;

/// <summary>
/// Consumer-driven contract tests (13.3). Each committed <c>*.contract.json</c> under <c>Contracts/</c> is the
/// agreed contract for a partner's inbound payload or outbound message. The test drives the partner's ACL adapter
/// with the fixture and asserts the mapping — so a change to an adapter that breaks a partner contract FAILS CI.
/// (Pact-equivalent: fixtures are the shared contract; no broker needed for the on-prem, offline-first stack.)
/// </summary>
public class ContractTests
{
    private static readonly Dictionary<string, IInboundIntegrationAdapter> Inbound =
        new(StringComparer.Ordinal) { ["digital-referral-network"] = new ReferralNetworkAdapter() };
    private static readonly Dictionary<string, IOutboundIntegrationAdapter> Outbound =
        new(StringComparer.Ordinal) { ["digital-referral-network"] = new ReferralNetworkAdapter() };

    public static IEnumerable<object[]> Contracts()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Contracts");
        foreach (var file in Directory.EnumerateFiles(dir, "*.contract.json"))
            yield return [Path.GetFileName(file)];
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Adapter_honours_the_partner_contract(string fixtureFile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Contracts", fixtureFile);
        var c = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var partner = c["partner"]!.GetValue<string>();
        var direction = c["direction"]!.GetValue<string>();
        var expect = c["expect"]!.AsObject();

        if (direction == "inbound")
        {
            var msg = c["message"]!.AsObject();
            var body = msg["body"] is JsonObject o ? o.ToJsonString() : msg["body"]!.GetValue<string>();
            var result = Inbound[partner].Translate(new InboundMessage(partner, msg["format"]!.GetValue<string>(), body));

            if (expect["quarantine"]?.GetValue<bool>() == true)
            {
                result.IsMapped.Should().BeFalse($"contract '{fixtureFile}' expects quarantine");
                if (expect["reasonContains"] is { } rc)
                    result.QuarantineReason.Should().Contain(rc.GetValue<string>());
            }
            else
            {
                result.IsMapped.Should().BeTrue($"contract '{fixtureFile}' expects a mapping");
                result.Mapped![0].Type.Should().Be(expect["type"]!.GetValue<string>());
                foreach (var token in expect["contains"]!.AsArray())
                    result.Mapped![0].PayloadJson.Should().Contain(token!.GetValue<string>());
            }
        }
        else // outbound
        {
            var evt = c["internalEvent"]!.AsObject();
            var outMsg = Outbound[partner].Map(new InternalDomainEvent(evt["type"]!.GetValue<string>(), evt["payload"]!.ToJsonString()));
            outMsg.Should().NotBeNull($"contract '{fixtureFile}' expects an outbound message");
            outMsg!.Format.Should().Be(expect["format"]!.GetValue<string>());
            foreach (var token in expect["contains"]!.AsArray())
                outMsg.Body.Should().Contain(token!.GetValue<string>());
        }
    }
}
