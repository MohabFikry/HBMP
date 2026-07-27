using FluentAssertions;
using Mersal.Events;

namespace Mersal.Events.Tests;

/// <summary>
/// Phase 18.B2 — the tenant a background consumer binds its RLS session to comes from the event envelope,
/// never from a constant. These assert the two halves that matter: a tenant present is read exactly, and a
/// tenant absent is <c>null</c> so the caller dead-letters instead of guessing.
/// </summary>
public class EventTenantTests
{
    [Fact]
    public void Reads_the_camel_case_tenant_the_platform_publishes()
    {
        EventTenant.Of("""{"tenantId":"11111111-1111-1111-1111-111111111111","beneficiaryId":"x"}""")
            .Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Theory]
    [InlineData("""{"tenant_id":"t-snake"}""", "t-snake")]
    [InlineData("""{"TenantId":"t-pascal"}""", "t-pascal")]
    public void Accepts_the_other_casings_a_hand_rolled_publisher_might_emit(string payload, string expected) =>
        EventTenant.Of(payload).Should().Be(expected);

    [Theory]
    [InlineData("""{"beneficiaryId":"x"}""")]           // no tenant at all
    [InlineData("""{"tenantId":""}""")]                  // present but empty — an empty GUC denies every row
    [InlineData("""{"tenantId":"   "}""")]               // whitespace is not a tenant
    [InlineData("""{"tenantId":null}""")]
    [InlineData("""{"tenantId":12345}""")]               // wrong type
    [InlineData("[1,2,3]")]                              // not an object
    [InlineData("not json at all")]                      // malformed — must not throw into the consumer loop
    [InlineData("")]
    public void Refuses_anything_that_is_not_an_unambiguous_tenant(string payload) =>
        EventTenant.Of(payload).Should().BeNull(
            "a consumer must dead-letter an unattributable message, not stamp it with a guessed tenant");

    [Fact]
    public void Prefers_the_platform_convention_when_more_than_one_casing_is_present()
    {
        // Ambiguity resolves deterministically rather than by JSON property order.
        EventTenant.Of("""{"TenantId":"pascal","tenantId":"camel","tenant_id":"snake"}""")
            .Should().Be("camel");
    }
}
