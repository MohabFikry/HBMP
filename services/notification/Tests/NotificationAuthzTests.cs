using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Notification.Tests;

/// <summary>Authorization proof for the notification surface (US-072 min-necessary): any authenticated role may read
/// its OWN inbox with the read scope (the handler row-filters by recipient), but the read scope is required; the
/// fan-out seam requires the system <c>notification:ingest</c> scope and is not reachable with a plain read scope.
/// Exercised against the real engine over <see cref="NotificationPolicies"/>.</summary>
public class NotificationAuthzTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(NotificationPolicies.Bundle(),
            new AuditClient(_outbox, new AuditClientContext("notification-test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, params string[] scopes) => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(scopes),
        TenantId = "t0", MfaSatisfied = true,
    };

    private static ResourceRef Res() => new() { Type = NotificationPolicies.Resource, TenantId = "t0" };

    [Theory]
    [InlineData("doctor")]
    [InlineData("beneficiary")]
    [InlineData("finance")]
    [InlineData("reception")]
    public async Task Any_authenticated_role_may_read_its_own_inbox_with_the_read_scope(string role)
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal(role, "notification:read"), NotificationPolicies.Read, Res()));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Inbox_read_requires_the_read_scope()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor"), NotificationPolicies.Read, Res()));
        d.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Fan_out_seam_requires_the_ingest_scope()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("system", "notification:ingest"), NotificationPolicies.Ingest, Res()));
        d.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_plain_read_scope_cannot_reach_the_fan_out_seam()
    {
        var d = await Engine().EvaluateAsync(new AuthzRequest(
            Principal("doctor", "notification:read"), NotificationPolicies.Ingest, Res()));
        d.IsAllowed.Should().BeFalse();
    }
}
