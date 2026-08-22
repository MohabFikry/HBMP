using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Policy.Api;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.7 — the payer as a record somebody administers (design 56).
///
/// <para>The surface these cover did not exist: a payer could be created and then never corrected, switched
/// off, or explained. So every test here is about a write that had no way to happen, or a refusal that had
/// nothing to refuse it. The two that matter most are the deactivation refused while the payer still funds
/// live policies — the one place this feature could quietly end thousands of people's cover — and the payer
/// restriction, which the query surface honoured and the payer list did not.</para>
/// </summary>
[Collection("policy-db")]
public class PayerAdministrationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- authoring the record ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_payer_carries_its_agreement_its_terms_and_its_people()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var terms = new PayerTermsInput("GRANT-7781", "AGR-2026-14",
                Today.AddDays(-30), Today.AddDays(300), 5_000_000m, "EGP", 30, "Quarterly", 90);
            var contacts = new PayerContacts(
                Primary: new PayerContact("Huda Salem", "Programme officer", "huda@example.org", "+20100"),
                Finance: new PayerContact("Karim Adel", "Settlements", "karim@example.org", null),
                // Deliberately blank: an entry with nothing in it must come back as ABSENT, not as a card
                // with a heading and four empty rows.
                Escalation: new PayerContact(null, null, "  ", null));

            var created = await admin.PostAsJsonAsync("/api/v1/payers",
                new CreatePayer($"PAY-{Suffix()}", "Hope Foundation", "مؤسسة الأمل", "Donor", contacts, terms, "Renews annually."),
                Web);
            created.StatusCode.Should().Be(HttpStatusCode.Created,
                await created.Content.ReadAsStringAsync());

            var view = await created.Content.ReadFromJsonAsync<PayerView>(Web);
            view!.Agreement.ExternalRef.Should().Be("GRANT-7781");
            view.Agreement.State.Should().Be(nameof(PayerAgreementState.InForce));
            view.Terms.Should().NotBeNull("a policy administrator may read contract terms");
            view.Terms!.FundingCeiling.Should().Be(5_000_000m);
            view.Terms.InvoicingCadence.Should().Be("Quarterly");
            view.Contacts!.Primary!.Name.Should().Be("Huda Salem");
            view.Contacts.Escalation.Should().BeNull("an entry with every field blank is not a contact");
            view.Notes.Should().Be("Renews annually.");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>The window is a fact about the contract; the status is a decision about the record. A grant
    /// that ran its course still shows as an ACTIVE payer whose agreement has expired, because that
    /// combination is the one somebody has to act on rather than a state to hide.</summary>
    [SkippableFact]
    public async Task An_agreement_that_ran_its_course_reads_as_expired_and_the_payer_stays_active()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var id = await PayerAsync(admin, terms: new PayerTermsInput(
                null, null, Today.AddDays(-400), Today.AddDays(-35), null, null, null, null, null));

            var detail = await admin.GetFromJsonAsync<PayerDetailView>(
                new Uri($"/api/v1/payers/{id}", UriKind.Relative), Web);
            detail!.Payer.Status.Should().Be(nameof(CatalogStatus.Active));
            detail.Payer.Agreement.State.Should().Be(nameof(PayerAgreementState.Expired));
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_ceiling_of_zero_is_refused_because_it_is_not_the_same_as_uncapped()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var r = await admin.PostAsJsonAsync("/api/v1/payers",
                new CreatePayer($"PAY-{Suffix()}", "Zero", "صفر", "Donor",
                    null, new PayerTermsInput(null, null, null, null, 0m, null, null, null, null), null), Web);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync()).Should().Contain("uncapped");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- correcting it -----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task An_update_corrects_the_names_the_type_and_the_terms_and_never_the_code()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var id = await PayerAsync(admin);
            var before = await admin.GetFromJsonAsync<PayerDetailView>(
                new Uri($"/api/v1/payers/{id}", UriKind.Relative), Web);

            var r = await admin.PutAsJsonAsync($"/api/v1/payers/{id}",
                new UpdatePayer("Corrected name", "الاسم المصحّح", "PartnerNGO",
                    null, new PayerTermsInput(null, "AGR-9", null, null, 250_000m, "usd", 45, "Monthly", 60), "note"),
                Web);
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

            var after = await r.Content.ReadFromJsonAsync<PayerView>(Web);
            after!.NameEn.Should().Be("Corrected name");
            after.PayerType.Should().Be("PartnerNGO");
            after.Terms!.FundingCeiling.Should().Be(250_000m);
            after.Terms.Currency.Should().Be("USD", "a currency is normalized to its ISO code, not stored as typed");
            after.PayerCode.Should().Be(before!.Payer.PayerCode,
                "the code is the key extracts and the payer's own systems join on — an update has no field for it");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_member_administrator_may_not_author_a_payer_at_all()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var id = await PayerAsync(admin);

            using var memberAdmin = app.MemberAdminClient();
            (await memberAdmin.PutAsJsonAsync($"/api/v1/payers/{id}",
                new UpdatePayer("x", "س", "Donor"), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await memberAdmin.PostAsJsonAsync($"/api/v1/payers/{id}/deactivate",
                new ChangePayerStatus("The grant closed at the end of the funding year."), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await memberAdmin.GetAsync(new Uri($"/api/v1/payers/{id}/history", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- switching it off --------------------------------------------------------------------------------

    /// <summary>
    /// The refusal this whole feature turns on. Deactivating a payer that still funds live policies would
    /// leave every one of them resolving against a counterparty the platform has been told is finished, and
    /// nothing downstream would say so — so the write is refused, with the count, rather than cascading.
    /// </summary>
    [SkippableFact]
    public async Task Deactivation_is_refused_while_the_payer_still_funds_an_active_policy()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var payerId = await PayerAsync(admin);
            await PolicyAsync(admin, payerId);

            var r = await admin.PostAsJsonAsync($"/api/v1/payers/{payerId}/deactivate",
                new ChangePayerStatus("The funding agreement ended on 31 December."), Web);

            r.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await r.Content.ReadAsStringAsync();
            body.Should().Contain("1 active policy",
                "the refusal has to say how much is riding on it, or it reads as an unexplained no");

            var still = await admin.GetFromJsonAsync<PayerDetailView>(
                new Uri($"/api/v1/payers/{payerId}", UriKind.Relative), Web);
            still!.Payer.Status.Should().Be(nameof(CatalogStatus.Active));
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Deactivation_records_the_reason_and_reactivation_needs_one_of_its_own()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var id = await PayerAsync(admin);

            var off = await admin.PostAsJsonAsync($"/api/v1/payers/{id}/deactivate",
                new ChangePayerStatus("The 2025 grant closed and will not be renewed."), Web);
            off.StatusCode.Should().Be(HttpStatusCode.OK, await off.Content.ReadAsStringAsync());
            var view = await off.Content.ReadFromJsonAsync<PayerView>(Web);
            view!.Status.Should().Be(nameof(CatalogStatus.Inactive));
            view.StatusReason.Should().Be("The 2025 grant closed and will not be renewed.");
            view.StatusChangedAt.Should().NotBeNull();

            (await admin.PostAsJsonAsync($"/api/v1/payers/{id}/deactivate",
                new ChangePayerStatus("Trying it a second time to be sure."), Web))
                .StatusCode.Should().Be(HttpStatusCode.Conflict, "it is already inactive");

            var on = await admin.PostAsJsonAsync($"/api/v1/payers/{id}/reactivate",
                new ChangePayerStatus("The donor signed a renewal for the 2026 cycle."), Web);
            on.StatusCode.Should().Be(HttpStatusCode.OK);
            (await on.Content.ReadFromJsonAsync<PayerView>(Web))!.Status.Should().Be(nameof(CatalogStatus.Active));
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>A one-word reason is indistinguishable from no reason at all to whoever reads this record next
    /// year, and being readable then is the entire point of requiring one.</summary>
    [SkippableFact]
    public async Task A_status_change_with_no_readable_reason_is_refused()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var id = await PayerAsync(admin);

            foreach (var reason in new[] { "", "   ", "old" })
            {
                var r = await admin.PostAsJsonAsync($"/api/v1/payers/{id}/deactivate",
                    new ChangePayerStatus(reason), Web);
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "reason '{0}' explains nothing", reason);
            }
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- the history twin --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_history_records_every_change_newest_first_with_the_actor()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var id = await PayerAsync(admin);
            (await admin.PutAsJsonAsync($"/api/v1/payers/{id}",
                new UpdatePayer("Renamed once", "مرة", "Donor",
                    null, new PayerTermsInput(null, null, null, null, 900_000m, null, null, null, null), null), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.PostAsJsonAsync($"/api/v1/payers/{id}/deactivate",
                new ChangePayerStatus("Closing this counterparty at the donor's request."), Web))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var page = await admin.GetFromJsonAsync<PayerHistoryPage>(
                new Uri($"/api/v1/payers/{id}/history", UriKind.Relative), Web);

            page!.Entries.Should().HaveCountGreaterThanOrEqualTo(3, "create, rename, deactivate");
            page.Entries[0].Status.Should().Be(nameof(CatalogStatus.Inactive), "newest first");
            page.Entries[0].StatusReason.Should().Be("Closing this counterparty at the donor's request.");
            page.Entries.Should().Contain(e => e.FundingCeiling == 900_000m,
                "the ceiling that was set is what somebody comes to this screen to find");
            page.Entries[^1].Operation.Should().Be("INSERT", "the oldest entry is the day it was created");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- what the caller is allowed to see ---------------------------------------------------------------

    /// <summary>The commercial half is withheld as a BLOCK. Nulling it field by field would render as "not
    /// recorded", which is a different — and wrong — answer to give somebody about a ceiling that exists.</summary>
    [SkippableFact]
    public async Task A_caller_who_may_not_read_contract_terms_gets_no_terms_block_at_all()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var id = await PayerAsync(admin, terms: new PayerTermsInput(
                null, null, null, null, 400_000m, "EGP", 30, "Monthly", 90));

            // A pharmacist holds policy:read for pricing and is nobody's contract reader.
            using var dispenser = app.As("sub-pharmacist", "pharmacist", "policy:read");
            var detail = await dispenser.GetFromJsonAsync<PayerDetailView>(
                new Uri($"/api/v1/payers/{id}", UriKind.Relative), Web);

            detail!.Payer.Terms.Should().BeNull();
            detail.Payer.Agreement.Should().NotBeNull("whether the funding is still running is operational");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>19.5 restricts a user to a set of payers, and the query surface honoured it while the payer
    /// list did not — so a user scoped to one donor could read the whole counterparty list. A named payer
    /// outside the set is refused with 403 rather than 404: an empty page reads as "no such payer".</summary>
    [SkippableFact]
    public async Task A_payer_restricted_caller_sees_only_their_own_and_is_refused_the_rest()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var mine = await PayerAsync(admin);
            var theirs = await PayerAsync(admin);

            app.Payers = PermittedPayers.RestrictedTo([mine]);

            var list = await admin.GetFromJsonAsync<List<PayerView>>(
                new Uri("/api/v1/payers", UriKind.Relative), Web);
            list!.Select(p => p.PayerId).Should().BeEquivalentTo([mine]);

            (await admin.GetAsync(new Uri($"/api/v1/payers/{theirs}", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await admin.PutAsJsonAsync($"/api/v1/payers/{theirs}",
                new UpdatePayer("x", "س", "Donor"), Web))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await admin.GetAsync(new Uri($"/api/v1/payers/{theirs}/history", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- the book of business ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_detail_counts_what_actually_hangs_off_this_payer()
    {
        Skip.If(PolicyApiFactory.Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PolicyApiFactory();
        try
        {
            using var admin = app.ProductAdminClient();
            var payerId = await PayerAsync(admin, terms: new PayerTermsInput(
                null, null, null, null, 1_000_000m, "EGP", null, null, null));
            await PolicyAsync(admin, payerId);
            await PolicyAsync(admin, payerId);

            // A second payer with its own policy, so the counts prove they are scoped rather than global.
            var other = await PayerAsync(admin);
            await PolicyAsync(admin, other);

            var detail = await admin.GetFromJsonAsync<PayerDetailView>(
                new Uri($"/api/v1/payers/{payerId}", UriKind.Relative), Web);

            detail!.Book.PolicyCount.Should().Be(2);
            detail.Book.ActivePolicyCount.Should().Be(2);
            detail.Book.MemberCount.Should().Be(0, "nobody has been enrolled yet");
            detail.Book.CommittedLimit.Should().Be(0m,
                "zero committed is the honest answer for a payer nobody is enrolled under; null means withheld");
            detail.Book.CeilingPercentCommitted.Should().Be(0m);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);
    private static string Suffix() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<Guid> PayerAsync(HttpClient admin, PayerTermsInput? terms = null)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/payers",
            new CreatePayer($"PAY-{Suffix()}", "Test payer", "دافع اختبار", "Donor", null, terms, null), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created,
            "the seed must succeed or every assertion below is vacuous: {0}", await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payerId").GetGuid();
    }

    private static async Task<Guid> PolicyAsync(HttpClient admin, Guid payerId)
    {
        var r = await admin.PostAsJsonAsync("/api/v1/policies",
            new CreatePolicy($"POL-{Suffix()}", payerId, Today, null, null), Web);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("policyId").GetGuid();
    }
}
