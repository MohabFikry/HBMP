using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.8 — the plan and the contract as administrable records (design 57).
///
/// <para>The two entities beneath the payer had the identical gap 19.7 closed: a create, a list, and nothing
/// else. What these hold is mostly the writes that had no way to happen — but the two that matter are the
/// pair of REFUSALS that are deliberately different from each other, because the domains are:</para>
///
/// <list type="bullet">
///   <item>Deactivating a plan still attached to an active policy is REFUSED. It is a catalogue action, and
///   withdrawing a product members are being enrolled onto would strand those enrolments.</item>
///   <item>Suspending a policy with active members is ALLOWED, and reports the count. It is the operation
///   itself — the thing that happens when a payer stops paying — and refusing it would refuse the
///   operation.</item>
/// </list>
/// </summary>
[Collection("policy-db")]
public class PlanAndPolicyAdministrationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // PLAN
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task A_plans_names_category_and_description_can_be_corrected_and_its_code_cannot()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, code) = await PlanAsync(admin);

            var r = await admin.PutAsJsonAsync($"/api/v1/plans/{planId}",
                new UpdatePlan("Corrected plan", "الخطة المصححة", "Now with a description.", "Premium"), Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

            var after = await r.Content.ReadFromJsonAsync<PlanAdminView>(Web);
            after!.NameEn.Should().Be("Corrected plan");
            after.Category.Should().Be("Premium");
            after.Description.Should().Be("Now with a description.");
            after.PlanCode.Should().Be(code,
                "the code is what extracts and the payer's systems join on — an update has no field for it");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_plan_needs_a_name_in_both_languages()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, _) = await PlanAsync(admin);
            (await admin.PutAsJsonAsync($"/api/v1/plans/{planId}",
                new UpdatePlan("Only English", "  ", null, "Standard"), Web))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The refusal. A plan attached to an active policy is a product members are being enrolled
    /// onto; withdrawing it from the catalogue would leave those enrolments resolving against a product the
    /// catalogue says is gone.</summary>
    [SkippableFact]
    public async Task Deactivating_a_plan_is_refused_while_an_active_policy_still_sells_it()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, _) = await SoldPlanAsync(app, admin);

            var r = await admin.PostAsJsonAsync($"/api/v1/plans/{planId}/deactivate",
                new ChangePolicyStatus("Withdrawing this product from the 2026 catalogue."), Web);

            r.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await r.Content.ReadAsStringAsync()).Should().Contain("1 active policy");

            var still = await admin.GetFromJsonAsync<PlanDetailView>(
                new Uri($"/api/v1/plans/{planId}", UriKind.Relative), Web);
            still!.Plan.Status.Should().Be(nameof(CatalogStatus.Active));
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_plan_nobody_sells_deactivates_with_its_reason_and_reactivates_again()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, _) = await PlanAsync(admin);

            (await admin.PostAsJsonAsync($"/api/v1/plans/{planId}/deactivate",
                new ChangePolicyStatus("Never sold — superseded before it was ever attached."), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var off = await admin.GetFromJsonAsync<PlanDetailView>(
                new Uri($"/api/v1/plans/{planId}", UriKind.Relative), Web);
            off!.Plan.Status.Should().Be(nameof(CatalogStatus.Inactive));
            off.Plan.StatusReason.Should().StartWith("Never sold");

            (await admin.PostAsJsonAsync($"/api/v1/plans/{planId}/deactivate",
                new ChangePolicyStatus("Trying a second time to be sure."), Web))
                .StatusCode.Should().Be(HttpStatusCode.Conflict, "it is already inactive");

            (await admin.PostAsJsonAsync($"/api/v1/plans/{planId}/reactivate",
                new ChangePolicyStatus("Brought back for the 2027 catalogue."), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_plan_status_change_with_no_readable_reason_is_refused()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, _) = await PlanAsync(admin);
            foreach (var reason in new[] { "", "   ", "old" })
            {
                (await admin.PostAsJsonAsync($"/api/v1/plans/{planId}/deactivate",
                    new ChangePolicyStatus(reason), Web))
                    .StatusCode.Should().Be(HttpStatusCode.BadRequest, "reason '{0}' explains nothing", reason);
            }
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_plan_detail_counts_its_versions_and_what_is_sold_against_them()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, _) = await SoldPlanAsync(app, admin);

            var detail = await admin.GetFromJsonAsync<PlanDetailView>(
                new Uri($"/api/v1/plans/{planId}", UriKind.Relative), Web);

            detail!.Book.VersionCount.Should().Be(1);
            detail.Book.ActiveCount.Should().Be(1);
            detail.Book.PolicyCount.Should().Be(1);
            detail.Book.ActivePolicyCount.Should().Be(1);
            detail.Book.FirstEffectiveFrom.Should().NotBeNull("a version exists, so the window has a start");
            detail.Book.LastEffectiveTo.Should().BeNull("the version is open-ended, so there is no last day");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_plan_history_records_every_change_newest_first_with_the_actor()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, _) = await PlanAsync(admin);
            (await admin.PutAsJsonAsync($"/api/v1/plans/{planId}",
                new UpdatePlan("Renamed once", "مرة", null, "Premium"), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.PostAsJsonAsync($"/api/v1/plans/{planId}/deactivate",
                new ChangePolicyStatus("Withdrawn at the end of the catalogue year."), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var page = await admin.GetFromJsonAsync<PlanHistoryPage>(
                new Uri($"/api/v1/plans/{planId}/history", UriKind.Relative), Web);

            page!.Entries.Should().HaveCountGreaterThanOrEqualTo(3, "create, rename, deactivate");
            page.Entries[0].Status.Should().Be(nameof(CatalogStatus.Inactive), "newest first");
            page.Entries[0].ActorName.Should().NotBeNullOrWhiteSpace();
            page.Entries.Should().Contain(e => e.Category == "Premium");
            page.Entries[^1].Operation.Should().Be("INSERT", "the oldest entry is the day it was created");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Only_a_product_administrator_may_write_a_plan()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var (planId, _) = await PlanAsync(admin);

            using var memberAdmin = app.MemberAdminClient();
            (await memberAdmin.PutAsJsonAsync($"/api/v1/plans/{planId}",
                new UpdatePlan("x", "س", null, "Standard"), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await memberAdmin.PostAsJsonAsync($"/api/v1/plans/{planId}/deactivate",
                new ChangePolicyStatus("Trying it from the wrong desk."), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // POLICY (the contract)
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task A_contracts_window_cap_and_notes_can_be_renegotiated()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var policyId = await PolicyAsync(admin);

            var r = await admin.PutAsJsonAsync($"/api/v1/policies/{policyId}",
                new UpdatePolicy(Today, Today.AddDays(365), 500, null, "Cap raised at the March review."), Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

            var after = await r.Content.ReadFromJsonAsync<PolicyAdminView>(Web);
            after!.EffectiveTo.Should().Be(Today.AddDays(365));
            after.WindowState.Should().Be(nameof(PolicyWindowState.InForce));
            after.Terms!.MaxMembers.Should().Be(500);
            after.Terms.Notes.Should().Be("Cap raised at the March review.");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_backwards_window_and_a_cap_of_zero_are_both_refused()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var policyId = await PolicyAsync(admin);

            (await admin.PutAsJsonAsync($"/api/v1/policies/{policyId}",
                new UpdatePolicy(Today, Today.AddDays(-1), null, null, null), Web))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var zero = await admin.PutAsJsonAsync($"/api/v1/policies/{policyId}",
                new UpdatePolicy(Today, null, 0, null, null), Web);
            zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await zero.Content.ReadAsStringAsync()).Should().Contain("closed to enrolment");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// The counterpart to the plan's refusal, and the opposite answer. Suspending IS the operation, so it
    /// proceeds — and returns how many people it reached, so the confirmation can state the impact rather
    /// than the operator discovering it afterwards.
    /// </summary>
    [SkippableFact]
    public async Task Suspending_a_contract_proceeds_and_reports_how_many_members_it_reached()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var policyId = await PolicyAsync(admin);

            var r = await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/suspend",
                new ChangePolicyStatus("The payer missed the February settlement."), Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

            var result = await r.Content.ReadFromJsonAsync<PolicyStatusResult>(Web);
            result!.Policy.Status.Should().Be(nameof(PolicyStatus.Suspended));
            result.Policy.StatusReason.Should().Be("The payer missed the February settlement.");
            result.Policy.StatusChangedAt.Should().NotBeNull();
            result.ActiveMembersAffected.Should().Be(0, "nobody is enrolled on this fixture");

            (await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/resume",
                new ChangePolicyStatus("Settlement received; cover restored."), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>Expired is where a contract ends, not a state it passes through. Resuming one would silently
    /// re-open cover for everybody it ended; the way back is a renewal, which somebody issues deliberately.</summary>
    [SkippableFact]
    public async Task An_expired_contract_is_renewed_rather_than_resumed()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var policyId = await PolicyAsync(admin);

            (await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/expire",
                new ChangePolicyStatus("Ran to its end date and was not renewed."), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var back = await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/resume",
                new ChangePolicyStatus("Trying to bring it back the easy way."), Web);
            back.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await back.Content.ReadAsStringAsync()).Should().Contain("Renew it");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_contract_status_change_with_no_readable_reason_is_refused()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var policyId = await PolicyAsync(admin);
            foreach (var reason in new[] { "", "   ", "unpaid" })
            {
                (await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/suspend",
                    new ChangePolicyStatus(reason), Web))
                    .StatusCode.Should().Be(HttpStatusCode.BadRequest, "reason '{0}' explains nothing", reason);
            }
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_contract_history_records_the_suspension_and_its_reason()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var policyId = await PolicyAsync(admin);
            (await admin.PutAsJsonAsync($"/api/v1/policies/{policyId}",
                new UpdatePolicy(Today, null, 250, null, null), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/suspend",
                new ChangePolicyStatus("The payer missed two consecutive settlements."), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var page = await admin.GetFromJsonAsync<PolicyHistoryPage>(
                new Uri($"/api/v1/policies/{policyId}/history", UriKind.Relative), Web);

            page!.Entries.Should().HaveCountGreaterThanOrEqualTo(3, "create, amend, suspend");
            page.Entries[0].Status.Should().Be(nameof(PolicyStatus.Suspended), "newest first");
            page.Entries[0].StatusReason.Should().Be("The payer missed two consecutive settlements.");
            page.Entries.Should().Contain(e => e.MaxMembers == 250);
            page.Entries[^1].Operation.Should().Be("INSERT");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The 19.5 restriction narrowed the register and, until now, not the records in it: every route
    /// here addresses one policy by id, so the rule has to be applied per row.</summary>
    [SkippableFact]
    public async Task A_payer_restricted_caller_is_refused_a_contract_outside_their_set()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var policyId = await PolicyAsync(admin);

            app.Payers = PermittedPayers.RestrictedTo([Guid.NewGuid()]);

            (await admin.GetAsync(new Uri($"/api/v1/policies/{policyId}", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await admin.PutAsJsonAsync($"/api/v1/policies/{policyId}",
                new UpdatePolicy(Today, null, null, null, null), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/suspend",
                new ChangePolicyStatus("Should never get this far."), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await admin.GetAsync(new Uri($"/api/v1/policies/{policyId}/history", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_caller_who_may_not_read_contract_terms_gets_no_terms_block_at_all()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var policyId = await PolicyAsync(admin);

            // A pharmacist holds policy:read for pricing and is nobody's contract reader.
            using var dispenser = app.As("sub-pharmacist", "pharmacist", "policy:read");
            var detail = await dispenser.GetFromJsonAsync<PolicyDetailView>(
                new Uri($"/api/v1/policies/{policyId}", UriKind.Relative), Web);

            detail!.Policy.Terms.Should().BeNull();
            detail.Policy.WindowState.Should().NotBeNull("whether the contract is in force is operational");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);
    private static string Suffix() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<Guid> PayerAsync(HttpClient admin)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/payers",
            new CreatePayer($"PAY-{Suffix()}", "Test payer", "دافع اختبار", "Donor"), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payerId").GetGuid();
    }

    private static async Task<Guid> PolicyAsync(HttpClient admin)
    {
        var payerId = await PayerAsync(admin);
        var r = await admin.PostAsJsonAsync("/api/v1/policies",
            new CreatePolicy($"POL-{Suffix()}", payerId, Today, null, null), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("policyId").GetGuid();
    }

    private static async Task<(Guid PlanId, string PlanCode)> PlanAsync(HttpClient admin)
    {
        var code = $"PLAN-{Suffix()}";
        var r = await admin.PostAsJsonAsync("/api/v1/plans",
            new CreatePlan(code, "Test plan", "خطة اختبار", null, "Standard"), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return ((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("planId").GetGuid(), code);
    }

    /// <summary>A plan with an ACTIVE version attached to an ACTIVE policy — the state the deactivation
    /// refusal is about, built through the endpoints so every gate between them is exercised.</summary>
    private static async Task<(Guid PlanId, Guid PolicyId)> SoldPlanAsync(PolicyApiFactory app, HttpClient admin)
    {
        var (planId, _) = await PlanAsync(admin);

        var version = await admin.PostAsJsonAsync("/api/v1/plan-versions",
            new CreatePlanVersion(planId, Today, null), Web);
        version.StatusCode.Should().Be(HttpStatusCode.Created, await version.Content.ReadAsStringAsync());
        var versionId = (await version.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("planVersionId").GetGuid();

        var categories = await admin.GetFromJsonAsync<List<BenefitCategoryView>>(
            new Uri("/api/v1/benefit-categories", UriKind.Relative), Web);
        var code = categories![0].Code;

        var rules = await admin.PutAsJsonAsync($"/api/v1/plan-versions/{versionId}/rules",
            new SetBenefitRules([
                new BenefitRuleInput(code, IsCovered: true, LimitType: "Annual", LimitValue: 10_000m,
                    ResetPeriod: "Yearly", Deductible: null, DeductibleWaived: false, WaitingPeriodDays: 0,
                    RequiresPreauth: false, PreauthCostThreshold: null, Exclusions: null, Notes: null,
                    Tiers: [new BenefitRuleTierInput(FakeTierCatalog.TierId, IsCovered: true, CopayFixed: 20m,
                        CopayPercent: null, CoinsurancePercent: null, CopayCountsTowardDeductible: false,
                        RequiresPreauthOverride: null, LimitMultiplier: null)]),
            ]), Web);
        rules.StatusCode.Should().Be(HttpStatusCode.OK, await rules.Content.ReadAsStringAsync());

        (await admin.PostAsync(new Uri($"/api/v1/plan-versions/{versionId}/activate", UriKind.Relative), null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var policyId = await PolicyAsync(admin);
        var attached = await admin.PostAsJsonAsync($"/api/v1/policies/{policyId}/plans",
            new AttachPolicyPlan(versionId, "Standard", Today, null, IsDefault: true, null, null), Web);
        attached.StatusCode.Should().Be(HttpStatusCode.Created, await attached.Content.ReadAsStringAsync());

        _ = app;
        return (planId, policyId);
    }
}
