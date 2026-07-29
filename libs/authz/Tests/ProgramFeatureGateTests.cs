using System.Security.Claims;
using FluentAssertions;
using Mersal.Auth;
using Mersal.Authz;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Authz.Tests;

/// <summary>
/// 21.4 — the third gate at its call sites (design 40 §4, adaptation A4).
///
/// What these pin is not "does it compare a string" but the three rules that make it safe to switch on across
/// eleven services: it can only SUBTRACT, it evaluates every principal that carries a tenant, and its refusal
/// is a DIFFERENT problem type from an authorization denial — because the remedies differ, and sending someone
/// to Mersal for something their own administrator controls (or the reverse) wastes both.
/// </summary>
public class ProgramFeatureGateTests
{
    private static ClaimsPrincipal Principal(string? tenant, params string[] features)
    {
        var claims = new List<Claim> { new("sub", "u-1") };
        if (tenant is not null) claims.Add(new Claim(HbmpClaimTypes.TenantId, tenant));
        claims.AddRange(features.Select(f => new Claim(HbmpClaimTypes.Features, f)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    // ---- The endpoint filter -----------------------------------------------------------------------------

    private static async Task<(bool ranHandler, object? result)> InvokeFilterAsync(
        ClaimsPrincipal user, string gatedOn)
    {
        var http = new DefaultHttpContext { User = user };
        var context = new DefaultEndpointFilterInvocationContext(http);
        var ran = false;

        var result = await new ProgramFeatureFilter(gatedOn)
            .InvokeAsync(context, _ => { ran = true; return ValueTask.FromResult<object?>(Results.Ok("executed")); });

        return (ran, result);
    }

    [Fact]
    public async Task An_enabled_tenant_reaches_the_endpoint()
    {
        var (ran, _) = await InvokeFilterAsync(
            Principal("t-1", ProgramFeatures.Claims, ProgramFeatures.Emr), ProgramFeatures.Claims);

        ran.Should().BeTrue();
    }

    /// <summary>The refusal must be distinguishable from a permission denial — the SPA keys a different
    /// treatment off the type, and the two send the user to different people.</summary>
    [Fact]
    public async Task A_tenant_not_on_the_programme_is_refused_with_its_own_problem_type()
    {
        var (ran, result) = await InvokeFilterAsync(Principal("t-1", ProgramFeatures.Emr), ProgramFeatures.Claims);

        ran.Should().BeFalse("the handler must not execute");
        var problem = result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var details = result.Should().BeAssignableTo<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>().Subject;
        details.ProblemDetails.Type.Should().Be(ProgramEnablement.NotEnabledType);
        details.ProblemDetails.Extensions["code"].Should().Be(ProgramEnablement.NotEnabledCode);
        // The feature is named so support can act without a follow-up question, and so the SPA can say WHICH
        // programme rather than showing a generic wall.
        details.ProblemDetails.Extensions["feature"].Should().Be(ProgramFeatures.Claims);
    }

    /// <summary>A token minted before the claim existed enables nothing — the gate's default must be closed, or
    /// the oldest token in circulation defeats it.</summary>
    [Fact]
    public async Task A_token_with_no_features_claim_is_refused()
    {
        var (ran, _) = await InvokeFilterAsync(Principal("t-1"), ProgramFeatures.Claims);
        ran.Should().BeFalse();
    }

    /// <summary>
    /// The carve-out, pinned so it cannot widen: a principal with NO tenant is not subject to enablement,
    /// because it belongs to no organisation. This is what keeps the event pipeline's client-credentials ingest
    /// calls working — refusing them would stop the platform's own machinery for every tenant rather than
    /// enforce a policy.
    /// </summary>
    [Fact]
    public async Task A_tenant_less_principal_is_not_subject_to_the_gate()
    {
        var (ran, _) = await InvokeFilterAsync(Principal(tenant: null), ProgramFeatures.Claims);
        ran.Should().BeTrue();
    }

    /// <summary>...and the carve-out is EXACTLY that. Any principal carrying a tenant is evaluated, whatever
    /// else it holds; an empty tenant string is not a licence.</summary>
    [Theory]
    [InlineData("t-1")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public async Task Any_principal_with_a_tenant_is_evaluated(string tenant)
    {
        var (ran, _) = await InvokeFilterAsync(Principal(tenant), ProgramFeatures.Claims);
        ran.Should().BeFalse($"tenant {tenant} is not on the programme");
    }

    [Fact]
    public void A_gate_with_no_feature_key_is_a_programming_error_not_an_open_gate()
    {
        var act = () => new ProgramFeatureFilter("");
        act.Should().Throw<ArgumentException>();
    }

    // ---- The whole-service middleware --------------------------------------------------------------------

    private static async Task<(bool reachedApp, int status)> InvokeMiddlewareAsync(
        ClaimsPrincipal user, string path, string gatedOn, params string[] exempt)
    {
        // A real provider, not a stub: writing a problem+json response resolves the problem-details and
        // logging services out of RequestServices, so a null provider fails inside the refusal path — which is
        // the path under test.
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var app = new ApplicationBuilderStub(services);
        app.UseProgramFeature(gatedOn, exempt);

        var reached = false;
        var pipeline = app.Build(_ => { reached = true; return Task.CompletedTask; });

        var http = new DefaultHttpContext { User = user, RequestServices = services };
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        await pipeline(http);

        return (reached, http.Response.StatusCode);
    }

    [Fact]
    public async Task The_middleware_refuses_a_disabled_module_and_admits_an_enabled_one()
    {
        var off = await InvokeMiddlewareAsync(Principal("t-1", ProgramFeatures.Emr), "/api/v1/claims", ProgramFeatures.Claims);
        off.reachedApp.Should().BeFalse();
        off.status.Should().Be(StatusCodes.Status403Forbidden);

        var on = await InvokeMiddlewareAsync(Principal("t-1", ProgramFeatures.Claims), "/api/v1/claims", ProgramFeatures.Claims);
        on.reachedApp.Should().BeTrue();
    }

    /// <summary>Health probes are anonymous, which is why the ten services needed no path exemption for them.
    /// A gate that broke liveness would take a disabled module's container down rather than refuse its
    /// requests.</summary>
    [Fact]
    public async Task An_anonymous_request_passes_so_health_probes_keep_working()
    {
        var (reached, _) = await InvokeMiddlewareAsync(Anonymous(), "/health/live", ProgramFeatures.Claims);
        reached.Should().BeTrue();
    }

    [Fact]
    public async Task The_middleware_honours_an_explicit_path_exemption()
    {
        var (reached, _) = await InvokeMiddlewareAsync(
            Principal("t-1"), "/api/v1/claims/still-allowed", ProgramFeatures.Claims, "/api/v1/claims/still-allowed");
        reached.Should().BeTrue();
    }

    [Fact]
    public async Task The_middleware_applies_the_same_tenant_less_carve_out_as_the_filter()
    {
        var (reached, _) = await InvokeMiddlewareAsync(Principal(tenant: null), "/api/v1/claims", ProgramFeatures.Claims);
        reached.Should().BeTrue();
    }
}

/// <summary>Minimal IEndpointFilterInvocationContext — the framework's own implementation is internal.</summary>
internal sealed class DefaultEndpointFilterInvocationContext(HttpContext httpContext) : EndpointFilterInvocationContext
{
    public override HttpContext HttpContext { get; } = httpContext;
    public override IList<object?> Arguments { get; } = [];
    public override T GetArgument<T>(int index) => throw new NotSupportedException();
}

/// <summary>Minimal IApplicationBuilder so the middleware can be exercised without a host.</summary>
internal sealed class ApplicationBuilderStub(IServiceProvider services) : Microsoft.AspNetCore.Builder.IApplicationBuilder
{
    private readonly List<Func<RequestDelegate, RequestDelegate>> _components = [];

    public IServiceProvider ApplicationServices { get; set; } = services;
    public IFeatureCollection ServerFeatures { get; } = new FeatureCollection();
    public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>();

    public Microsoft.AspNetCore.Builder.IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
    {
        _components.Add(middleware);
        return this;
    }

    public Microsoft.AspNetCore.Builder.IApplicationBuilder New() => new ApplicationBuilderStub(ApplicationServices);

    public RequestDelegate Build() => Build(_ => Task.CompletedTask);

    public RequestDelegate Build(RequestDelegate terminal)
    {
        var next = terminal;
        for (var i = _components.Count - 1; i >= 0; i--) next = _components[i](next);
        return next;
    }
}
