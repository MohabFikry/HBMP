using System.Security.Claims;
using FluentAssertions;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Mersal.Auth.Tests;

public class ScopeAuthorizationHandlerTests
{
    private sealed class RecordingSink : IAuthEventSink
    {
        public List<AuthEvent> Events { get; } = [];
        public void Record(AuthEvent evt) => Events.Add(evt);
    }

    private static ClaimsPrincipal User(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static AuthorizationHandlerContext Context(ScopeRequirement req, ClaimsPrincipal user) =>
        new([req], user, resource: null);

    [Fact]
    public async Task Allows_when_scope_present_and_mfa_satisfied()
    {
        var sink = new RecordingSink();
        var handler = new ScopeAuthorizationHandler(sink);
        var req = new ScopeRequirement("orders:consume", requireMfa: true);
        var ctx = Context(req, User(
            new Claim("sub", "u"),
            new Claim("scope", "orders:consume"),
            new Claim("amr", "otp")));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
        sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Denies_and_audits_when_scope_present_but_mfa_missing()
    {
        var sink = new RecordingSink();
        var handler = new ScopeAuthorizationHandler(sink);
        var req = new ScopeRequirement("orders:consume", requireMfa: true);
        var ctx = Context(req, User(
            new Claim("sub", "u"),
            new Claim("scope", "orders:consume"),
            new Claim("amr", "pwd")));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
        ctx.HasFailed.Should().BeTrue();
        sink.Events.Should().ContainSingle()
            .Which.Kind.Should().Be(AuthEventKind.MfaRequiredButMissing);
    }

    [Fact]
    public async Task Denies_and_audits_when_scope_missing()
    {
        var sink = new RecordingSink();
        var handler = new ScopeAuthorizationHandler(sink);
        var req = new ScopeRequirement("auth:decide", requireMfa: false);
        var ctx = Context(req, User(
            new Claim("sub", "u"),
            new Claim("scope", "reception:read")));

        await handler.HandleAsync(ctx);

        ctx.HasFailed.Should().BeTrue();
        sink.Events.Should().ContainSingle()
            .Which.Kind.Should().Be(AuthEventKind.AuthorizationDenied);
    }

    [Fact]
    public async Task Unauthenticated_user_is_not_succeeded()
    {
        var handler = new ScopeAuthorizationHandler(new RecordingSink());
        var req = new ScopeRequirement("orders:consume", requireMfa: false);
        var ctx = Context(req, new ClaimsPrincipal(new ClaimsIdentity())); // not authenticated

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }
}
