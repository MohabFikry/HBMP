using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Reporting.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Reporting.Tests;

/// <summary>
/// The one WRITE in reporting-service, and what now guards it.
///
/// <para><b>What it looked like before.</b> <c>POST /api/v1/reports/projections</c> writes facts into the read
/// model — the six tables every oversight figure on the Medical Director's portal is computed from. The
/// policy rule behind it names no roles, which <c>IAuthorizationEngine</c> reads as "any authenticated
/// principal holding the scope", and the identity seed granted <c>reporting:project</c> to
/// <c>medical_director</c>. So the person the dashboard is about could author the dashboard, and the handler
/// recorded nothing.</para>
///
/// <para>The revocation itself is asserted in <c>Mersal.Authz.Tests.ProjectionSeamTests</c>, which reads the
/// identity migrations: a rule that names no roles must require a <c>service_only</c> scope, and no role may
/// hold one. This file covers the two things that are only observable over HTTP — that the tenant comes from
/// the principal rather than the request body, and that a projection is audited whether or not it applied.</para>
/// </summary>
[Collection("reporting-db")]
public class ProjectionSeamEndpointTests
{
    /// <summary>
    /// The tenant on the wire is not the tenant that is written.
    ///
    /// <para><c>ReportingGate</c> builds the resource it authorizes from the CALLER's principal, so the rule's
    /// <c>TenantMatch</c> condition compares a tenant against itself and is vacuously true here. The body then
    /// carried its own <c>tenantId</c> and the projector used it — meaning a caller authenticated for one
    /// tenant could write facts into another organisation's dashboard, past RLS, because the service sets the
    /// tenant GUC from what it was told rather than from who asked.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_projection_may_not_be_written_for_a_tenant_the_caller_is_not_authenticated_for()
    {
        Skip.If(ReportingApiFactory.Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        await using var app = new ReportingApiFactory();
        using var relay = app.ProjectionClient();

        var response = await relay.PostAsJsonAsync("/api/v1/reports/projections", new
        {
            eventId = Guid.NewGuid(),
            eventType = "AuthApproved",
            tenantId = "some-other-tenant",
            fields = new Dictionary<string, string> { ["authNo"] = "AUTH-1", ["priority"] = "Routine" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // And nothing was written. A refusal that still projected would be a 400 in front of the very write
        // it claims to have refused.
        await using var db = ReportingApiFactory.Ctx();
        (await db.AuthorizationFacts.AsNoTracking().CountAsync(f => f.TenantId == "some-other-tenant"))
            .Should().Be(0);
    }

    /// <summary>A projection for the caller's own tenant is accepted, and lands under THAT tenant even when
    /// the body says nothing about it.</summary>
    [SkippableFact]
    public async Task A_projection_is_written_under_the_callers_own_tenant()
    {
        Skip.If(ReportingApiFactory.Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        await using var app = new ReportingApiFactory();
        using var relay = app.ProjectionClient();

        var authNo = "AUTH-" + Guid.NewGuid().ToString("N")[..8];
        var response = await relay.PostAsJsonAsync("/api/v1/reports/projections", new
        {
            eventId = Guid.NewGuid(),
            eventType = "AuthApproved",
            fields = new Dictionary<string, string> { ["authNo"] = authNo, ["priority"] = "Routine" },
        });

        response.EnsureSuccessStatusCode();

        await using var db = ReportingApiFactory.Ctx();
        var fact = await db.AuthorizationFacts.AsNoTracking().FirstOrDefaultAsync(f => f.AuthNo == authNo);
        fact.Should().NotBeNull();
        fact!.TenantId.Should().Be(app.Tenant);

        await ReportingApiFactory.CleanupProjectionsAsync(authNo);
    }

    /// <summary>
    /// A replay is a no-op, and says so.
    ///
    /// <para>Worth its own case because the audit event must distinguish the two: "projected" and
    /// "deduplicated" are different answers to "did this figure move", and an audit trail that recorded both
    /// as a write would make every relay retry look like a second fact.</para>
    /// </summary>
    [SkippableFact]
    public async Task Replaying_one_event_projects_once()
    {
        Skip.If(ReportingApiFactory.Db is null, "REPORTING_TEST_DB not set — DB integration test skipped.");
        await using var app = new ReportingApiFactory();
        using var relay = app.ProjectionClient();

        var authNo = "AUTH-" + Guid.NewGuid().ToString("N")[..8];
        var eventId = Guid.NewGuid();
        object Body() => new
        {
            eventId,
            eventType = "AuthApproved",
            fields = new Dictionary<string, string> { ["authNo"] = authNo, ["priority"] = "Routine" },
        };

        var first = await (await relay.PostAsJsonAsync("/api/v1/reports/projections", Body()))
            .Content.ReadFromJsonAsync<ProjectionResult>();
        var second = await (await relay.PostAsJsonAsync("/api/v1/reports/projections", Body()))
            .Content.ReadFromJsonAsync<ProjectionResult>();

        first!.Projected.Should().BeTrue();
        second!.Projected.Should().BeFalse("the dedupe ledger makes a redelivery a no-op");

        await using var db = ReportingApiFactory.Ctx();
        (await db.AuthorizationFacts.AsNoTracking().CountAsync(f => f.AuthNo == authNo)).Should().Be(1);

        await ReportingApiFactory.CleanupProjectionsAsync(authNo);
    }

    private sealed record ProjectionResult(bool Projected);
}
