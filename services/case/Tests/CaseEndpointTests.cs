using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Case.Api;
using Mersal.Case.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Case.Tests;

/// <summary>
/// Phase 24 Gate 3 — the case access model, over HTTP.
///
/// <para>case-service's Api layer measured 0.0%, and the access model lives entirely in it: an ASSIGNMENT is
/// the ABAC anchor, so assigning grants a case manager access to a case and unassigning revokes it. A
/// manager who keeps reading a case after being unassigned, or one who can read a case they were never
/// assigned to, is the failure this suite exists to catch — and neither would have failed a test.</para>
/// </summary>
[Collection("case-db")]
public class CaseEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The whole anchor, in one test because the three states only mean something in sequence: a manager
    /// cannot read a case they hold no assignment to; assigning grants it; unassigning takes it away again.
    /// </summary>
    [SkippableFact]
    public async Task Assignment_grants_access_to_a_case_and_unassignment_revokes_it()
    {
        Skip.If(CaseApiFactory.Db is null, "CASE_TEST_DB not set — DB integration test skipped.");
        await using var app = new CaseApiFactory();
        try
        {
            using var supervisor = app.SupervisorClient();
            var caseId = await OpenCaseAsync(supervisor);

            using var manager = app.ManagerClient(CaseTestAuth.OtherManagerSub);
            (await manager.GetAsync(Case(caseId))).StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "no assignment is no access — the ABAC anchor is the assignment row, not the role");

            var assigned = await supervisor.PostAsJsonAsync($"/api/v1/cases/{caseId}/assign",
                new AssignRequest(Guid.Parse(CaseTestAuth.OtherManagerSub)), Web);
            assigned.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await assigned.Content.ReadAsStringAsync());
            (await manager.GetAsync(Case(caseId))).StatusCode.Should().Be(HttpStatusCode.OK);

            var unassigned = await supervisor.PostAsJsonAsync($"/api/v1/cases/{caseId}/unassign",
                new UnassignRequest(Guid.Parse(CaseTestAuth.OtherManagerSub)), Web);
            unassigned.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await manager.GetAsync(Case(caseId))).StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "unassignment REVOKES access on the next request, not at some later sweep");

            await using var db = CaseApiFactory.Ctx();
            var row = await db.Assignments.AsNoTracking()
                .SingleAsync(a => a.CaseId == caseId && a.CaseManagerId == Guid.Parse(CaseTestAuth.OtherManagerSub));
            row.Active.Should().BeFalse();
            row.UnassignedAt.Should().NotBeNull("the revocation is stamped, not just switched off");

            app.Outbox.AllMessages.Select(m => m.EventType)
                .Should().Contain("CaseAssigned").And.Contain("CaseUnassigned");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Assigning_the_same_manager_twice_does_not_create_a_second_grant()
    {
        Skip.If(CaseApiFactory.Db is null, "CASE_TEST_DB not set — DB integration test skipped.");
        await using var app = new CaseApiFactory();
        try
        {
            using var supervisor = app.SupervisorClient();
            var caseId = await OpenCaseAsync(supervisor);
            var body = new AssignRequest(Guid.Parse(CaseTestAuth.ManagerSub));

            (await supervisor.PostAsJsonAsync($"/api/v1/cases/{caseId}/assign", body, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await supervisor.PostAsJsonAsync($"/api/v1/cases/{caseId}/assign", body, Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            await using var db = CaseApiFactory.Ctx();
            (await db.Assignments.CountAsync(a => a.CaseId == caseId && a.Active)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Opening a case requires a beneficiary. A case filed against nobody is a record that can never
    /// be found again from the person it is about.</summary>
    [SkippableFact]
    public async Task A_case_without_a_beneficiary_is_refused()
    {
        Skip.If(CaseApiFactory.Db is null, "CASE_TEST_DB not set — DB integration test skipped.");
        await using var app = new CaseApiFactory();
        try
        {
            using var supervisor = app.SupervisorClient();
            var r = await supervisor.PostAsJsonAsync("/api/v1/cases",
                new OpenCaseRequest(Guid.Empty, CaseCategory.Complex, null, "no beneficiary"), Web);
            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadAsStringAsync()).Should().Contain("beneficiary-required");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>An escalation moves the case out of OnHold and tells the target role it is owed a look. Both
    /// facts commit together, which is what makes the event worth asserting alongside the row.</summary>
    [SkippableFact]
    public async Task Escalating_records_the_escalation_and_announces_it()
    {
        Skip.If(CaseApiFactory.Db is null, "CASE_TEST_DB not set — DB integration test skipped.");
        await using var app = new CaseApiFactory();
        try
        {
            using var supervisor = app.SupervisorClient();
            var caseId = await OpenCaseAsync(supervisor);
            (await supervisor.PostAsJsonAsync($"/api/v1/cases/{caseId}/assign",
                new AssignRequest(Guid.Parse(CaseTestAuth.ManagerSub)), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            using var manager = app.ManagerClient();
            var escalated = await manager.PostAsJsonAsync($"/api/v1/cases/{caseId}/escalate",
                new EscalateRequest("medical_director", "needs a clinical decision"), Web);
            escalated.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await escalated.Content.ReadAsStringAsync());

            // Both fields are mandatory, and the refusal names which one is missing.
            var noRole = await manager.PostAsJsonAsync($"/api/v1/cases/{caseId}/escalate",
                new EscalateRequest("  ", "reason"), Web);
            noRole.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await noRole.Content.ReadAsStringAsync()).Should().Contain("role-required");

            var noReason = await manager.PostAsJsonAsync($"/api/v1/cases/{caseId}/escalate",
                new EscalateRequest("medical_director", "  "), Web);
            noReason.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await noReason.Content.ReadAsStringAsync()).Should().Contain("reason-required");

            await using var db = CaseApiFactory.Ctx();
            (await db.Escalations.CountAsync(e => e.CaseId == caseId)).Should().Be(1);
            app.Outbox.AllMessages.Select(m => m.EventType).Should().Contain("CaseEscalated");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- the 360 -------------------------------------------------------------------------------------------

    /// <summary>
    /// The coordination view is FAIL-CLOSED. When profile-service cannot assemble it the endpoint answers 502
    /// and discloses nothing — a partial view is a leak whose shape nobody reviewed, and 200-with-holes is the
    /// answer a caller would render as fact.
    /// </summary>
    [SkippableFact]
    public async Task The_360_is_refused_when_it_cannot_be_assembled_rather_than_partly_disclosed()
    {
        Skip.If(CaseApiFactory.Db is null, "CASE_TEST_DB not set — DB integration test skipped.");
        await using var app = new CaseApiFactory { Profile = null };
        try
        {
            using var supervisor = app.SupervisorClient();
            var caseId = await OpenCaseAsync(supervisor);
            (await supervisor.PostAsJsonAsync($"/api/v1/cases/{caseId}/assign",
                new AssignRequest(Guid.Parse(CaseTestAuth.ManagerSub)), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            using var manager = app.ManagerClient();
            var r = await manager.GetAsync(new Uri($"/api/v1/cases/{caseId}/beneficiary-360", UriKind.Relative));
            ((int)r.StatusCode).Should().Be(502);
            (await r.Content.ReadAsStringAsync()).Should().Contain("coordination-view-unavailable");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>An unassigned manager does not reach the 360 either — the case-assignment ABAC is asked
    /// before the assembler is ever called, so no cross-service read happens for a caller with no claim to
    /// the case.</summary>
    [SkippableFact]
    public async Task An_unassigned_manager_does_not_reach_the_360()
    {
        Skip.If(CaseApiFactory.Db is null, "CASE_TEST_DB not set — DB integration test skipped.");
        await using var app = new CaseApiFactory();
        try
        {
            using var supervisor = app.SupervisorClient();
            var caseId = await OpenCaseAsync(supervisor);

            using var manager = app.ManagerClient(CaseTestAuth.OtherManagerSub);
            (await manager.GetAsync(new Uri($"/api/v1/cases/{caseId}/beneficiary-360", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The eligibility override is delegated to eligibility-service, and its REASON is mandatory
    /// (FR-ELG-007): an override with no recorded reason is an entitlement decision nobody can review.</summary>
    [SkippableFact]
    public async Task An_eligibility_override_without_a_reason_is_refused()
    {
        Skip.If(CaseApiFactory.Db is null, "CASE_TEST_DB not set — DB integration test skipped.");
        await using var app = new CaseApiFactory();
        try
        {
            using var supervisor = app.SupervisorClient();
            var caseId = await OpenCaseAsync(supervisor);
            (await supervisor.PostAsJsonAsync($"/api/v1/cases/{caseId}/assign",
                new AssignRequest(Guid.Parse(CaseTestAuth.ManagerSub)), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            using var manager = app.ManagerClient();
            var noReason = await manager.PostAsJsonAsync($"/api/v1/cases/{caseId}/eligibility-override",
                new { eligible = true, reason = "   ", validUntil = (DateTimeOffset?)null }, Web);
            noReason.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await noReason.Content.ReadAsStringAsync()).Should().Contain("reason-required");

            var accepted = await manager.PostAsJsonAsync($"/api/v1/cases/{caseId}/eligibility-override",
                new { eligible = true, reason = "documented hardship", validUntil = (DateTimeOffset?)null }, Web);
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
            app.Outbox.AllMessages.Select(m => m.EventType).Should().Contain("EligibilityOverrideRequested");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_reaches_nothing_and_the_programme_gate_refuses_a_tenant_that_is_off()
    {
        Skip.If(CaseApiFactory.Db is null, "CASE_TEST_DB not set — DB integration test skipped.");
        await using var app = new CaseApiFactory();
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync(new Uri("/api/v1/cases", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // On other programmes, just not this one.
        using var offProgramme = app.As(CaseTestAuth.ManagerSub, "case_manager",
            "case:read case:read-list case:write case:open", features: Mersal.Authz.ProgramFeatures.Emr);
        (await offProgramme.GetAsync(new Uri("/api/v1/cases", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static Uri Case(Guid id) => new($"/api/v1/cases/{id}", UriKind.Relative);

    private static async Task<Guid> OpenCaseAsync(HttpClient client)
    {
        var r = await client.PostAsJsonAsync("/api/v1/cases",
            new OpenCaseRequest(Guid.NewGuid(), CaseCategory.Complex, CasePriority.Normal, "opened by a test"), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created,
            "the seed must succeed or every assertion below is vacuous: {0}", await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("caseId").GetGuid();
    }
}
