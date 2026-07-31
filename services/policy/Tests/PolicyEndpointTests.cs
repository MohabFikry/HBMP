using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 24 Gate 3 — entitlement, from authoring a product to enrolling a member, over HTTP.
///
/// <para>These are the rules that live in the endpoint layer and had no test: the separation between
/// authoring a product (policy:admin) and administering a member against it (policy:write); the refusal to
/// attach a Draft plan version, whose rules can still change under an enrolled member; the plan window that
/// must fall inside its policy's, or the plan offers cover on days the policy does not exist for; and the
/// unparseable eligibility rule that is refused rather than read as "no restriction".</para>
///
/// <para>The whole chain runs through the endpoints — payer, plan, version, rules, activate, policy, attach,
/// group, enrol — because that is the order a real administrator does it in and each step's output is the
/// next step's input. A fixture that inserted rows directly would prove none of the gates between them.</para>
/// </summary>
[Collection("policy-db")]
public class PolicyEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- the authoring gates ------------------------------------------------------------------------------

    /// <summary>
    /// The separation the whole policy set is built around: a member administrator may enrol, terminate and
    /// move people, and may not author the product they are enrolled onto. Holding policy:write is not
    /// holding policy:admin.
    /// </summary>
    [SkippableFact]
    public async Task A_member_administrator_cannot_author_the_product_they_enrol_members_onto()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var memberAdmin = app.MemberAdminClient();

            (await memberAdmin.PostAsJsonAsync("/api/v1/payers",
                new CreatePayer($"PX-{Suffix()}", "Payer", "دافع", "Insurer", null), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            (await memberAdmin.PostAsJsonAsync("/api/v1/plans",
                new CreatePlan($"PL-{Suffix()}", "Plan", "خطة", null, "Standard"), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // ...and a reader may do neither, nor administer a member.
            using var reader = app.ReaderClient();
            (await reader.PostAsJsonAsync("/api/v1/payers",
                new CreatePayer($"PY-{Suffix()}", "Payer", "دافع", "Insurer", null), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// A Draft version has never been in force and its rules are still editable. Attaching one would let a
    /// member be enrolled against a configuration that changes under them — so it is refused, and the same
    /// version attaches once it has been activated. Both halves, or the test only proves the endpoint says no
    /// to everything.
    /// </summary>
    [SkippableFact]
    public async Task A_draft_plan_version_cannot_be_attached_and_the_activated_one_can()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, versionId) = await DraftVersionAsync(app, admin);
            var policyId = await PolicyAsync(app, admin);

            var draft = await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/plans",
                new AttachPolicyPlan(versionId, "Standard", Today, null, true, null, null), Web);
            draft.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await draft.Content.ReadAsStringAsync()).Should().Contain("PLAN_VERSION_NOT_ACTIVE");

            (await admin.PostAsync(new Uri($"/api/v1/plan-versions/{versionId}/activate", UriKind.Relative), null))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var attached = await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/plans",
                new AttachPolicyPlan(versionId, "Standard", Today, null, true, null, null), Web);
            attached.StatusCode.Should().Be(HttpStatusCode.Created);
            _ = planId;
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A plan whose window starts before its policy's offers cover on days the policy does not exist
    /// for — every claim in that gap would be adjudicated against a policy that had not begun.</summary>
    [SkippableFact]
    public async Task A_plan_window_outside_the_policys_is_refused()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var versionId = await ActiveVersionAsync(app, admin);
            var policyId = await PolicyAsync(app, admin);

            var early = await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/plans",
                new AttachPolicyPlan(versionId, "Early", Today.AddDays(-30), null, false, null, null), Web);
            early.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await early.Content.ReadAsStringAsync()).Should().Contain("OUTSIDE_POLICY_WINDOW");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>An eligibility rule that cannot be parsed is REFUSED, not treated as "no restriction". The
    /// permissive reading is the dangerous one: it silently enrols everybody onto a plan whose author wrote a
    /// restriction they believed was in force.</summary>
    [SkippableFact]
    public async Task An_unparseable_eligibility_rule_is_refused_rather_than_read_as_no_restriction()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var versionId = await ActiveVersionAsync(app, admin);
            var policyId = await PolicyAsync(app, admin);

            var malformed = await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/plans",
                new AttachPolicyPlan(versionId, "Broken", Today, null, false, "age >= ", null), Web);
            malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await malformed.Content.ReadAsStringAsync()).Should().Contain("MALFORMED_ELIGIBILITY_RULE");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_policy_on_an_unknown_payer_is_refused_and_a_backwards_window_too()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();

            var unknownPayer = await admin.PostAsJsonAsync("/api/v1/policies",
                new CreatePolicy($"POL-{Suffix()}", Guid.NewGuid(), Today, null, null), Web);
            unknownPayer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await unknownPayer.Content.ReadAsStringAsync()).Should().Contain("UNKNOWN_PAYER");

            var payerId = await PayerAsync(app, admin);
            var backwards = await admin.PostAsJsonAsync("/api/v1/policies",
                new CreatePolicy($"POL-{Suffix()}", payerId, Today, Today.AddDays(-1), null), Web);
            backwards.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await backwards.Content.ReadAsStringAsync()).Should().Contain("BAD_WINDOW");

            var blankNo = await admin.PostAsJsonAsync("/api/v1/policies",
                new CreatePolicy("   ", payerId, Today, null, null), Web);
            blankNo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await blankNo.Content.ReadAsStringAsync()).Should().Contain("POLICY_NO_REQUIRED");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- enrolment ----------------------------------------------------------------------------------------

    /// <summary>
    /// The whole chain, ending in a member who is actually covered. The enrolment endpoint generates coverage
    /// from the attached plan version's rules, and publishes one CoverageChanged per category — the event
    /// eligibility-service builds its projection from, and the one whose absence left enrolled members
    /// answering "no active coverage" at the counter.
    /// </summary>
    [SkippableFact]
    public async Task Enrolling_a_member_generates_their_coverage_and_announces_each_category()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (policyId, _) = await PolicyWithPlanAsync(app, admin);

            using var memberAdmin = app.MemberAdminClient();
            var beneficiaryId = Guid.NewGuid();
            var enrolled = await PostAsync(memberAdmin, "/api/v1/enrollments", Guid.NewGuid().ToString(),
                new CreateEnrollment(beneficiaryId, policyId, null, null, "Principal", null, Today, null, 34, null));
            enrolled.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await enrolled.Content.ReadAsStringAsync());
            var enrollmentId = (await enrolled.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("enrollmentId").GetGuid();

            await using var db = PolicyApiFactory.Ctx();
            var coverages = await db.Coverages.AsNoTracking()
                .Where(c => c.BeneficiaryId == beneficiaryId).ToListAsync();
            coverages.Should().NotBeEmpty(
                "an enrolled member with no coverage row is refused at the counter for an entitlement the " +
                "plan grants, with nothing anywhere to explain it");

            var published = app.Outbox.AllMessages;
            published.Select(e => e.EventType).Should().Contain("MemberEnrolled");
            published.Count(e => e.EventType == "CoverageChanged")
                .Should().Be(coverages.Count, "eligibility-service builds its projection from CoverageChanged " +
                                              "and from nothing else — one per generated coverage, or the " +
                                              "member is covered here and uncovered everywhere that matters");

            // The enrolment reads back, and its event log records the enrolment as an EVENT, not an edit.
            var read = await memberAdmin.GetAsync(new Uri($"/api/v1/enrollments/{enrollmentId}", UriKind.Relative));
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            var events = await memberAdmin.GetFromJsonAsync<List<JsonElement>>(
                new Uri($"/api/v1/enrollments/{enrollmentId}/events", UriKind.Relative), Web);
            events.Should().NotBeNullOrEmpty();
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// An enrolment without an Idempotency-Key is refused, and a retry under the same key returns the SAME
    /// membership rather than a second one. A duplicate membership is not a cosmetic problem: it is a second
    /// set of coverage rows and a second set of limits for one person.
    /// </summary>
    [SkippableFact]
    public async Task Enrolment_requires_an_idempotency_key_and_a_retry_creates_no_second_membership()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (policyId, _) = await PolicyWithPlanAsync(app, admin);

            using var memberAdmin = app.MemberAdminClient();
            var beneficiaryId = Guid.NewGuid();
            var body = new CreateEnrollment(beneficiaryId, policyId, null, null, "Principal", null, Today, null, 34, null);

            var noKey = await PostAsync(memberAdmin, "/api/v1/enrollments", null, body);
            noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await noKey.Content.ReadAsStringAsync()).Should().Contain("IDEMPOTENCY_KEY_REQUIRED");

            var key = Guid.NewGuid().ToString();
            var first = await PostAsync(memberAdmin, "/api/v1/enrollments", key, body);
            first.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await first.Content.ReadAsStringAsync());
            var enrollmentId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollmentId").GetGuid();

            var retry = await PostAsync(memberAdmin, "/api/v1/enrollments", key, body);
            retry.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
            (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollmentId").GetGuid()
                .Should().Be(enrollmentId);

            await using var db = PolicyApiFactory.Ctx();
            (await db.Enrollments.CountAsync(e => e.BeneficiaryId == beneficiaryId)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A beneficiary patient-service does not report as Active is not enrolled. Enrolling a
    /// deceased or merged record is the failure this guard exists for.</summary>
    [SkippableFact]
    public async Task A_beneficiary_who_is_not_active_is_not_enrolled()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory { BeneficiaryStatus = "Deceased" };
        try
        {
            using var admin = app.ProductAdminClient();
            var (policyId, _) = await PolicyWithPlanAsync(app, admin);

            using var memberAdmin = app.MemberAdminClient();
            var r = await PostAsync(memberAdmin, "/api/v1/enrollments", Guid.NewGuid().ToString(),
                new CreateEnrollment(Guid.NewGuid(), policyId, null, null, "Principal", null, Today, null, 34, null));
            r.StatusCode.Should().BeOneOf(HttpStatusCode.UnprocessableEntity, HttpStatusCode.Conflict,
                HttpStatusCode.BadRequest);

            await using var db = PolicyApiFactory.Ctx();
            (await db.Enrollments.CountAsync(e => e.TenantId == app.Tenant)).Should().Be(0);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Terminating is an EVENT appended to the log, never an edit — a retro-effective change records
    /// both when it applies and when it was decided, and that gap is the whole reason the log exists.</summary>
    [SkippableFact]
    public async Task Terminating_appends_an_event_and_leaves_the_enrolment_readable()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (policyId, _) = await PolicyWithPlanAsync(app, admin);

            using var memberAdmin = app.MemberAdminClient();
            var enrolled = await PostAsync(memberAdmin, "/api/v1/enrollments", Guid.NewGuid().ToString(),
                new CreateEnrollment(Guid.NewGuid(), policyId, null, null, "Principal", null, Today, null, 34, null));
            enrolled.StatusCode.Should().Be(HttpStatusCode.Created, "{0}", await enrolled.Content.ReadAsStringAsync());
            var enrollmentId = (await enrolled.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("enrollmentId").GetGuid();

            await using (var db = PolicyApiFactory.Ctx())
            {
                var before = await db.EnrollmentEvents.AsNoTracking()
                    .CountAsync(e => e.EnrollmentId == enrollmentId);

                var terminated = await memberAdmin.PostAsJsonAsync(
                    $"/api/v1/enrollments/{enrollmentId}/terminate",
                    new TerminateEnrollment(Today.AddDays(30), "left the programme"), Web);
                terminated.StatusCode.Should().Be(HttpStatusCode.OK);

                await using var after = PolicyApiFactory.Ctx();
                (await after.EnrollmentEvents.AsNoTracking().CountAsync(e => e.EnrollmentId == enrollmentId))
                    .Should().BeGreaterThan(before, "a termination is appended, not written over the enrolment");
                var row = await after.Enrollments.AsNoTracking().SingleAsync(e => e.EnrollmentId == enrollmentId);
                row.IsDeleted.Should().BeFalse("membership history is never hard-deleted");
            }
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_reaches_nothing()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        using var anonymous = app.CreateClient();
        (await anonymous.GetAsync(new Uri("/api/v1/payers", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/v1/policies",
            new CreatePolicy("POL-X", Guid.NewGuid(), Today, null, null), Web))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- the authoring chain, as helpers ------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string? idempotencyKey, object body)
    {
        // Awaited inside the using: returning the task would dispose the content mid-send.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);
    private static string Suffix() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<Guid> PayerAsync(PolicyApiFactory app, HttpClient admin)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/payers",
            new CreatePayer($"PAY-{Suffix()}", "Test payer", "دافع اختبار", "Insurer", null), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created, "the seed must succeed or every assertion below is vacuous");
        _ = app;
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payerId").GetGuid();
    }

    private static async Task<Guid> PolicyAsync(PolicyApiFactory app, HttpClient admin)
    {
        var payerId = await PayerAsync(app, admin);
        var r = await admin.PostAsJsonAsync("/api/v1/policies",
            new CreatePolicy($"POL-{Suffix()}", payerId, Today, null, null), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("policyId").GetGuid();
    }

    /// <summary>A plan with a Draft version carrying one covered category, priced at the one Active tier —
    /// activation rejects a covered category that leaves any Active tier unpriced, so the grid is not
    /// optional.</summary>
    private static async Task<(Guid PlanId, Guid VersionId)> DraftVersionAsync(PolicyApiFactory app, HttpClient admin)
    {
        var plan = await admin.PostAsJsonAsync("/api/v1/plans",
            new CreatePlan($"PLAN-{Suffix()}", "Test plan", "خطة اختبار", null, "Standard"), Web);
        plan.StatusCode.Should().Be(HttpStatusCode.Created);
        var planId = (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("planId").GetGuid();

        var version = await admin.PostAsJsonAsync("/api/v1/plan-versions",
            new CreatePlanVersion(planId, Today, null), Web);
        version.StatusCode.Should().Be(HttpStatusCode.Created);
        var versionId = (await version.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("planVersionId").GetGuid();

        var categories = await admin.GetFromJsonAsync<List<BenefitCategoryView>>(
            new Uri("/api/v1/benefit-categories", UriKind.Relative), Web);
        categories.Should().NotBeNullOrEmpty("the benefit-category reference rows come from the migrations");
        var code = categories![0].Code;

        var rules = await admin.PutAsJsonAsync($"/api/v1/plan-versions/{versionId}/rules",
            new SetBenefitRules([
                new BenefitRuleInput(code, IsCovered: true, LimitType: "Annual", LimitValue: 10_000m,
                    ResetPeriod: "Yearly", // Waiving a deductible the plan does not have is refused by ck_benefit_rule_waiver_needs_deductible.
                    Deductible: null, DeductibleWaived: false, WaitingPeriodDays: 0,
                    RequiresPreauth: false, PreauthCostThreshold: null, Exclusions: null, Notes: null,
                    Tiers: [new BenefitRuleTierInput(FakeTierCatalog.TierId, IsCovered: true, CopayFixed: 20m,
                        CopayPercent: null, CoinsurancePercent: null, CopayCountsTowardDeductible: false,
                        RequiresPreauthOverride: null, LimitMultiplier: null)]),
            ]), Web);
        rules.StatusCode.Should().Be(HttpStatusCode.OK,
            "the rule set is the seed for everything below: {0}", await rules.Content.ReadAsStringAsync());
        _ = app;
        return (planId, versionId);
    }

    private static async Task<Guid> ActiveVersionAsync(PolicyApiFactory app, HttpClient admin)
    {
        var (_, versionId) = await DraftVersionAsync(app, admin);
        (await admin.PostAsync(new Uri($"/api/v1/plan-versions/{versionId}/activate", UriKind.Relative), null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return versionId;
    }

    private static async Task<(Guid PolicyId, Guid PolicyPlanId)> PolicyWithPlanAsync(
        PolicyApiFactory app, HttpClient admin)
    {
        var versionId = await ActiveVersionAsync(app, admin);
        var policyId = await PolicyAsync(app, admin);
        var attached = await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/plans",
            new AttachPolicyPlan(versionId, "Standard", Today, null, IsDefault: true, null, null), Web);
        attached.StatusCode.Should().Be(HttpStatusCode.Created);
        var policyPlanId = (await attached.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("policyPlanId").GetGuid();
        return (policyId, policyPlanId);
    }
}
