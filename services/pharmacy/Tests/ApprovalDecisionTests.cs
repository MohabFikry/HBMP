using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Events;
using Mersal.Pharmacy.Api;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// The RETURN leg of the prior-authorization saga, medication side — and it closes the sharpest gap in the
/// whole chain.
/// </summary>
/// <remarks>
/// <para><see cref="PrescriptionWorkflow.IsDispensable"/> admits only <c>Approved</c> and
/// <c>PartiallyDispensed</c>. The only path that ever set a prescription <c>Approved</c> was the auto-route at
/// creation — for scripts that needed no approval at all — because nothing in the platform consumed
/// <c>approvals.events</c>. So a prescription that WAS sent for approval could never become dispensable,
/// whatever the reviewer decided: the counter refused it, correctly, for ever, while the reviewer's screen
/// said Approved. The first test is that sentence made false.</para>
/// </remarks>
[Collection("pharmacy-db")]
public class ApprovalDecisionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string? Db = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB");
    private static readonly Guid Reviewer = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static PharmacyDbContext Ctx() =>
        new(new DbContextOptionsBuilder<PharmacyDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    // ---------------------------------------------------------------- the mirror (no DB needed)

    [Fact]
    public void Pharmacy_gets_its_own_copy_of_every_settling_decision()
    {
        ApprovalDecisionFeed.PharmacyQueue.Should().NotBe(ApprovalDecisionFeed.OrdersQueue);
        ApprovalDecisionFeed.Queues.Should().Contain(ApprovalDecisionFeed.PharmacyQueue);
        ApprovalDecisionFeed.Includes("AuthApproved").Should().BeTrue();
        ApprovalDecisionFeed.Includes("AuthRejected").Should().BeTrue();
        // A reviewer asking for more information leaves the script Submitted, which it already is.
        ApprovalDecisionFeed.Includes("AuthInfoRequested").Should().BeFalse();
    }

    // ---------------------------------------------------------------- the decision (DB)

    [SkippableFact]
    public async Task An_approved_prescription_becomes_dispensable()
    {
        Skip.If(Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, _) = await SeedSubmittedAsync(beneficiary);

            // The state it was stuck in. Not a set-up detail: this is the defect, asserted before the fix
            // runs, so the test cannot pass by the prescription having been dispensable all along.
            await using (var before = Ctx())
            {
                var rx = await before.Prescriptions.AsNoTracking().SingleAsync(p => p.PrescriptionId == rxId);
                PrescriptionWorkflow.IsDispensable(rx.Status).Should().BeFalse();
            }

            var authorizationId = Guid.NewGuid();
            var (result, outbox) = await ApplyAsync(Decision(rxId, authorizationId, releases: true));

            result.Outcome.Should().Be(ApprovalApplyOutcome.Released);

            await using var db = Ctx();
            var after = await db.Prescriptions.AsNoTracking().SingleAsync(p => p.PrescriptionId == rxId);
            after.Status.Should().Be(RxStatus.Approved);
            PrescriptionWorkflow.IsDispensable(after.Status).Should().BeTrue("the counter may now hand it over");
            after.AuthorizationId.Should().Be(authorizationId);

            // The SAME event the auto-route emits, distinguished by a flag rather than by a second name: two
            // names for one fact would make every consumer handle both to answer "is this script live?".
            var approved = outbox.AllMessages.Single(m => m.EventType == "RxApproved");
            JsonDocument.Parse(approved.Payload).RootElement.GetProperty("auto").GetBoolean().Should().BeFalse();
        }
        finally { await CleanupAsync(beneficiary); }
    }

    [SkippableFact]
    public async Task A_rejection_settles_the_script_instead_of_leaving_it_waiting_for_ever()
    {
        Skip.If(Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, _) = await SeedSubmittedAsync(beneficiary);

            var (result, outbox) = await ApplyAsync(Decision(rxId, Guid.NewGuid(), releases: false));

            result.Outcome.Should().Be(ApprovalApplyOutcome.Rejected);

            await using var db = Ctx();
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .SingleAsync(p => p.PrescriptionId == rxId);
            rx.Status.Should().Be(RxStatus.Rejected);
            PrescriptionWorkflow.IsDispensable(rx.Status).Should().BeFalse();

            // The lines are untouched: they were not withdrawn, the request was refused, and IsDispensable
            // already excludes Rejected.
            rx.Lines.Should().OnlyContain(l => l.Status == RxLineStatus.Active);
            outbox.AllMessages.Select(m => m.EventType).Should().Contain("RxRejected");
        }
        finally { await CleanupAsync(beneficiary); }
    }

    [SkippableFact]
    public async Task A_partial_approval_leaves_the_patient_with_the_drugs_that_were_allowed()
    {
        Skip.If(Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, drugs) = await SeedSubmittedAsync(beneficiary, lines: 2);

            var (result, _) = await ApplyAsync(
                Decision(rxId, Guid.NewGuid(), releases: true, scope: [drugs[0].ToString()]));

            result.Outcome.Should().Be(ApprovalApplyOutcome.Released);

            await using var db = Ctx();
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .SingleAsync(p => p.PrescriptionId == rxId);

            // Dispensable, with one drug on it. Sending the patient away with nothing because one item was
            // declined is exactly the outcome partial approval exists to avoid.
            rx.Status.Should().Be(RxStatus.Approved);
            rx.Lines.Single(l => l.DrugId == drugs[0]).Status.Should().Be(RxLineStatus.Active);

            var refused = rx.Lines.Single(l => l.DrugId == drugs[1]);
            refused.Status.Should().Be(RxLineStatus.Cancelled);
            // WHY, WHO and WHEN — ck_rx_line_amendment_attributed refuses the write without all three, and
            // the actor is the REVIEWER whose decision it was, not the consumer that carried it.
            refused.AmendmentReasonCode.Should().Be("not-in-approved-scope");
            refused.AmendedBy.Should().Be(Reviewer);
            refused.AmendedAt.Should().NotBeNull();
        }
        finally { await CleanupAsync(beneficiary); }
    }

    [SkippableFact]
    public async Task A_full_approval_carries_no_scope_and_cancels_nothing()
    {
        Skip.If(Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, _) = await SeedSubmittedAsync(beneficiary, lines: 2);

            // The dangerous reading of an absent scope is "nothing was approved", which would empty a fully
            // approved script on a missing field. approvals sends a scope only for a PARTIAL approval.
            await ApplyAsync(Decision(rxId, Guid.NewGuid(), releases: true, scope: null));

            await using var db = Ctx();
            var rx = await db.Prescriptions.AsNoTracking().Include(p => p.Lines)
                .SingleAsync(p => p.PrescriptionId == rxId);
            rx.Lines.Should().OnlyContain(l => l.Status == RxLineStatus.Active);
        }
        finally { await CleanupAsync(beneficiary); }
    }

    [SkippableFact]
    public async Task A_redelivered_decision_does_not_approve_the_script_twice()
    {
        Skip.If(Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var (rxId, _) = await SeedSubmittedAsync(beneficiary);
            var msg = Decision(rxId, Guid.NewGuid(), releases: true);

            await ApplyAsync(msg);
            var (replay, outbox) = await ApplyAsync(msg);

            // The consumer's processed_event ledger catches a redelivered MESSAGE id. This is the guard that
            // holds when a redelivery arrives under a NEW id: Approved → Approved is not a legal transition,
            // so nothing moves and nothing is published.
            replay.Outcome.Should().Be(ApprovalApplyOutcome.NotWaiting);
            outbox.AllMessages.Should().BeEmpty();
        }
        finally { await CleanupAsync(beneficiary); }
    }

    [SkippableFact]
    public async Task An_investigation_order_decision_is_ignored_rather_than_dead_lettered()
    {
        Skip.If(Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");

        // Both decision queues receive every decision and each filters by source. Filtering costs a discarded
        // message; routing by payload at the relay would put approvals' vocabulary in the publisher.
        var (result, _) = await ApplyAsync(
            Decision(Guid.NewGuid(), Guid.NewGuid(), releases: true) with { Source = "OrderLine" });

        result.Outcome.Should().Be(ApprovalApplyOutcome.NotOurs);
    }

    // ---------------------------------------------------------------- the producer's half of the contract

    [SkippableFact]
    public async Task A_gated_prescription_publishes_everything_an_authorization_is_created_from()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        // The routing policy gates DrugA, so this script really goes for approval rather than
        // auto-approving — which is what puts `requiresApproval: true` on the wire.
        await using var app = new PrescribingApiFactory();
        app.GatedDrugIds.Add(app.DrugA);
        try
        {
            using var doctor = app.Prescriber();

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/prescriptions", UriKind.Relative))
            {
                Content = JsonContent.Create(new CreatePrescriptionRequest(
                    app.Beneficiary, app.Encounter, null, AcknowledgeAlerts: false,
                    Lines: [new CreateRxLine(app.DrugA, "500mg", "PO", "BD", 14, 0,
                        DurationDays: 7, ClientLineId: Guid.NewGuid())],
                    DiagnosisIcdCodes: ["E11.9"], Acknowledgements: []), options: Web),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var created = await doctor.SendAsync(request);
            created.StatusCode.Should().Be(HttpStatusCode.Created, "the seed itself must succeed or the assertions are vacuous");

            var outbox = (InMemoryOutbox)app.Services.GetRequiredService<InMemoryOutbox>();
            var submitted = outbox.AllMessages.Single(m => m.EventType == "RxSubmitted");
            using var doc = JsonDocument.Parse(submitted.Payload);
            var p = doc.RootElement;

            // These field names are approvals' `RoutingMessage`. An omission here does not degrade the
            // feature — the consumer dead-letters a message it cannot attribute, so the script silently never
            // reaches a reviewer, which is the state this whole change exists to end.
            p.GetProperty("tenantId").GetString().Should().NotBeNullOrWhiteSpace();
            p.GetProperty("prescriptionId").GetGuid().Should().NotBeEmpty();
            p.GetProperty("beneficiaryId").GetGuid().Should().Be(app.Beneficiary);
            p.GetProperty("encounterId").GetGuid().Should().Be(app.Encounter);
            p.GetProperty("orderedByUserId").GetString().Should().NotBeNullOrWhiteSpace();
            p.GetProperty("rxNo").GetString().Should().StartWith("RX-");

            // The flag the consumer routes on. RxSubmitted fires for EVERY prescription; this is what
            // separates the few that need a decision from the many that do not.
            p.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();

            // The requested drugs, as ids — the same vocabulary `AuthorizationScope.Assess` compares an
            // amendment against, so a partial approval and an out-of-scope check cannot disagree about what
            // was approved.
            p.GetProperty("serviceCodes").EnumerateArray().Select(e => e.GetString())
                .Should().BeEquivalentTo([app.DrugA.ToString()]);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- helpers

    private static ApprovalDecisionMessage Decision(
        Guid rxId, Guid authorizationId, bool releases, string[]? scope = null) =>
        new("11111111-1111-1111-1111-111111111111", authorizationId, "AUTH-2026-000123", "Prescription",
            rxId.ToString(), releases, scope, false, Reviewer);

    private static async Task<(ApprovalApplyResult Result, InMemoryOutbox Outbox)> ApplyAsync(ApprovalDecisionMessage msg)
    {
        await using var ctx = Ctx();
        var outbox = new InMemoryOutbox();
        var result = await new PrescriptionApprovalApplier(ctx, outbox, TimeProvider.System)
            .ApplyAsync(msg, CancellationToken.None);
        return (result, outbox);
    }

    /// <summary>A prescription in the state routing leaves a gated one in: <c>Submitted</c>, which
    /// <c>IsDispensable</c> excludes.</summary>
    private static async Task<(Guid RxId, Guid[] Drugs)> SeedSubmittedAsync(Guid beneficiary, int lines = 1)
    {
        await using var ctx = Ctx();
        var drugs = Enumerable.Range(0, lines).Select(_ => Guid.NewGuid()).ToArray();
        var rx = new Prescription
        {
            PrescriptionId = Guid.NewGuid(),
            RxNo = RxNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("rx_seq", 2026)),
            BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), PrescriberId = Guid.NewGuid(),
            Status = RxStatus.Submitted, SubmittedAt = DateTimeOffset.UtcNow,
            Lines = drugs.Select(d => new PrescriptionLine
            {
                PrescriptionLineId = Guid.NewGuid(), DrugId = d, DrugName = "Test drug",
                Dose = "500mg", Route = "PO", Frequency = "BD", QuantityPrescribed = 14m,
            }).ToList(),
        };
        ctx.Prescriptions.Add(rx);
        await ctx.SaveChangesAsync();
        return (rx.PrescriptionId, drugs);
    }

    private static async Task CleanupAsync(Guid beneficiary)
    {
        if (Db is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM pharmacy.prescription_line WHERE prescription_id IN " +
            "  (SELECT prescription_id FROM pharmacy.prescription WHERE beneficiary_id = {0}); " +
            "DELETE FROM pharmacy.prescription WHERE beneficiary_id = {0};", beneficiary);
    }
}
