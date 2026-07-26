using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mersal.Authz.Tests;

/// <summary>16.6 (H5): the runtime break-glass provider resolves the caller's active grants from admin and widens
/// access only within the grant's window + scope, forwarding the caller's bearer, and FAILS CLOSED (no grant) when
/// there is no token / no HTTP context / an error. Cross-service behaviour (engine allows an otherwise-denied read
/// under an active grant with break-glass audit) is covered by AuthorizationEngineTests' break-glass cases.</summary>
public class HttpBreakGlassProviderTests
{
    private const string Subject = "u-42";

    private static HttpBreakGlassProvider Provider(string? json, bool withToken = true, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(json, status);
        var factory = new StubFactory(handler);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (withToken) accessor.HttpContext!.Request.Headers.Authorization = "Bearer tok-123";
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new HttpBreakGlassProvider(factory, accessor, cache, NullLogger<HttpBreakGlassProvider>.Instance);
    }

    private static HbmpRequestContext Ctx(DateTimeOffset now, string type = "encounter", string? id = "ENC-1") =>
        new(Subject, new ResourceRef { Type = type, Id = id, TenantId = "t0" }, now);

    private static string Grant(DateTimeOffset notBefore, DateTimeOffset expires, string type = "encounter") =>
        $"[{{\"grantId\":\"{Guid.NewGuid()}\",\"notBefore\":\"{notBefore:o}\",\"expiresAt\":\"{expires:o}\",\"scopedResourceTypes\":[\"{type}\"],\"scopedResourceIds\":[]}}]";

    [Fact]
    public void Active_in_scope_grant_widens_access()
    {
        var now = DateTimeOffset.UtcNow;
        var g = Provider(Grant(now.AddMinutes(-5), now.AddMinutes(30))).ActiveGrantFor(Ctx(now));
        g.Should().NotBeNull();
        g!.SubjectUserId.Should().Be(Subject);
    }

    [Fact]
    public void Expired_grant_does_not_widen()
    {
        var now = DateTimeOffset.UtcNow;
        Provider(Grant(now.AddHours(-2), now.AddHours(-1))).ActiveGrantFor(Ctx(now)).Should().BeNull();
    }

    [Fact]
    public void Grant_for_a_different_resource_type_does_not_widen()
    {
        var now = DateTimeOffset.UtcNow;
        var p = Provider(Grant(now.AddMinutes(-5), now.AddMinutes(30), type: "prescription"));
        p.ActiveGrantFor(Ctx(now, type: "encounter")).Should().BeNull();
    }

    [Fact]
    public void No_bearer_fails_closed()
    {
        var now = DateTimeOffset.UtcNow;
        Provider(Grant(now.AddMinutes(-5), now.AddMinutes(30)), withToken: false).ActiveGrantFor(Ctx(now)).Should().BeNull();
    }

    [Fact]
    public void Admin_error_fails_closed()
    {
        var now = DateTimeOffset.UtcNow;
        Provider(json: null, status: HttpStatusCode.InternalServerError).ActiveGrantFor(Ctx(now)).Should().BeNull();
    }

    private sealed class StubHandler(string? json, HttpStatusCode status) : HttpMessageHandler
    {
        private HttpResponseMessage Build() => new(status)
        {
            Content = new StringContent(json ?? "[]", Encoding.UTF8, "application/json"),
        };
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => Build();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Build());
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("http://admin-service:8080") };
    }
}
