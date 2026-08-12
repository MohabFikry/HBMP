using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Events;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// The shortage the counter could not report. Design 49 §5, migration 0020.
/// </summary>
/// <remarks>
/// <para><c>POST …/lines/{id}/out-of-stock</c> has been complete since phase 6.3 — it consumes nothing, so
/// the unfilled quantity stays available; it notifies the PRESCRIBER on a route that escalates to the
/// pharmacy supervisor after eight hours; it audits. Nothing in the SPA called it, and the flag it raised
/// was stored nowhere, so the <c>outOfStock</c> boolean the web contract declares as a first-class field
/// existed only as a literal <c>false</c> in the HTTP client and a literal <c>true</c> in one dev fixture.
/// The feature rendered in development and in the tests and could not render in production.</para>
/// <para>These tests cover what changed: the flag persists and is reported; re-raising it notifies nobody a
/// second time; a dispense ends it; and the accumulator is untouched throughout, because out of stock is a
/// fact about the pharmacy rather than about the prescription.</para>
/// </remarks>
[Collection("pharmacy-db")]
public class OutOfStockIsRecordedTests
{
    private static readonly Guid Dispenser = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Pharmacy = new("44444444-4444-4444-4444-444444444444");

    private static HttpClient Counter(PrescribingApiFactory app)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", Dispenser.ToString());
        c.DefaultRequestHeaders.Add("X-Test-Role", "pharmacist");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "11111111-1111-1111-1111-111111111111");
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
                // EXACTLY the scopes the issuer grants a pharmacist for this surface — `provider:read` included,
        // because the dispensing VIEW is gated on the provider-queue rule rather than on pharmacy's own
        // (identity 0005 grants it). A fixture more generous than the issuer tests a system nobody runs; one
        // meaner than the issuer fails on a rule that would never have fired.
        c.DefaultRequestHeaders.Add("X-Test-Scope", "pharmacy:read pharmacy:dispense provider:read");
        // The dispensing pharmacy — `DispensingGate` refuses a caller with no provider before it consults
        // any policy at all.
        c.DefaultRequestHeaders.Add("X-Test-Provider", Pharmacy.ToString());
        c.DefaultRequestHeaders.Add("X-Test-Features", "pharmacy");
        return c;
    }

    /// <summary>A dispensable prescription with one line, written straight to the store — this suite is about
    /// what happens AFTER a prescription exists, and routing it through the prescribing endpoints would make
    /// every assertion here depend on rules it is not testing.</summary>
    private static async Task<(Guid RxId, Guid LineId)> SeedAsync(PrescribingApiFactory app)
    {
        await using var db = PrescribingApiFactory.Ctx();
        var rxId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        db.Prescriptions.Add(new Prescription
        {
            PrescriptionId = rxId,
            TenantId = "11111111-1111-1111-1111-111111111111",
            RxNo = "RX-2026-" + Guid.NewGuid().ToString("N")[..6],
            BeneficiaryId = app.Beneficiary,
            EncounterId = app.Encounter,
            PrescriberId = Guid.NewGuid(),
            Status = RxStatus.Approved,
            SubmittedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(20),
            Lines =
            [
                new PrescriptionLine
                {
                    PrescriptionLineId = lineId,
                    TenantId = "11111111-1111-1111-1111-111111111111",
                    PrescriptionId = rxId,
                    DrugId = app.DrugA,
                    DrugName = "Amoxicillin 500mg",
                    QuantityPrescribed = 21,
                    QuantityDispensed = 0,
                    Status = RxLineStatus.Active,
                    RootLineId = lineId,
                },
            ],
        });
        await db.SaveChangesAsync();
        return (rxId, lineId);
    }

    private static async Task<PrescriptionLine> LineAsync(Guid lineId)
    {
        await using var db = PrescribingApiFactory.Ctx();
        return await db.Set<PrescriptionLine>().AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId);
    }

    /// <summary>
    /// The flag SURVIVES, and comes back on the dispensing view.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Storing it and not projecting it leaves the client exactly where it was — writing
    /// a literal because there is nothing to read.
    /// </remarks>
    [SkippableFact]
    public async Task A_reported_shortage_is_stored_and_reported_back_on_the_view()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);
            var client = Counter(app);

            var res = await client.PostAsJsonAsync(
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/out-of-stock",
                new { prescriptionLineId = lineId, quantity = 5m, note = "Supplier back-order until Sunday." });
            res.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var stored = await LineAsync(lineId);
            stored.OutOfStock.Should().BeTrue();
            stored.OutOfStockAt.Should().NotBeNull();
            stored.OutOfStockBy.Should().Be(Dispenser.ToString());
            stored.OutOfStockQty.Should().Be(5m);
            stored.OutOfStockNote.Should().Be("Supplier back-order until Sunday.");

            // And the counter can SEE it — the half that was missing from the server entirely.
            var view = await client.GetFromJsonAsync<Api.DispensableRxView>(
                $"/api/v1/prescriptions/{rxId}/dispensing");
            var line = view!.Lines.Single(l => l.PrescriptionLineId == lineId);
            line.OutOfStock.Should().BeTrue();
            line.OutOfStockNote.Should().Be("Supplier back-order until Sunday.");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// Reporting the same shortage twice notifies the prescriber ONCE (invariant 44).
    /// </summary>
    /// <remarks>
    /// The notification route is actionable and escalates to the pharmacy supervisor after eight hours. Two
    /// pharmacists reporting the same empty shelf — or one whose first request timed out — would otherwise
    /// put two of those in front of one prescriber, each with its own timer. A control whose cost grows with
    /// how often the counter is short is a control the counter learns not to use.
    /// </remarks>
    [SkippableFact]
    public async Task Reporting_the_same_line_twice_notifies_the_prescriber_once()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);
            var client = Counter(app);
            var outbox = (InMemoryOutbox)app.Services.GetService(typeof(InMemoryOutbox))!;

            var body = new { prescriptionLineId = lineId, quantity = (decimal?)null, note = "None on the shelf." };
            var first = await client.PostAsJsonAsync($"/api/v1/prescriptions/{rxId}/lines/{lineId}/out-of-stock", body);
            var second = await client.PostAsJsonAsync($"/api/v1/prescriptions/{rxId}/lines/{lineId}/out-of-stock", body);

            first.StatusCode.Should().Be(HttpStatusCode.Accepted);
            // Not a 409. Nothing went wrong — the second pharmacist's screen should end up showing the flag
            // either way, and only the notification is withheld.
            second.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var events = outbox.AllMessages.Where(m => m.EventType == "RxLineOutOfStock").ToList();
            events.Should().HaveCount(2, "one domain event and one notification copy, from the FIRST report");
            events.Count(m => m.Destination == "notification.domain-events").Should().Be(1);
            events.Count(m => m.Destination == "pharmacy.events").Should().Be(1);

            // The recorded time is the FIRST report's — the shortage started then, and ageing it from the
            // second would reset the clock the escalation and any purchasing question run on.
            var stored = await LineAsync(lineId);
            stored.OutOfStockNote.Should().Be("None on the shelf.");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// Reporting a shortage does NOT consume anything.
    /// </summary>
    /// <remarks>
    /// This is the endpoint's founding promise and the reason it is not a state transition: the patient can
    /// come back for it. If flagging moved the accumulator or the line status, the quantity would be gone and
    /// the recovery would need an amendment.
    /// </remarks>
    [SkippableFact]
    public async Task Reporting_a_shortage_consumes_nothing()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);
            var client = Counter(app);

            // Asserted, not assumed. Every remaining assertion in this test is trivially true if the
            // request never reached the handler, so a refused call would report a pass.
            var res = await client.PostAsJsonAsync($"/api/v1/prescriptions/{rxId}/lines/{lineId}/out-of-stock",
                new { prescriptionLineId = lineId, quantity = (decimal?)null, note = (string?)null });
            res.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var stored = await LineAsync(lineId);
            stored.OutOfStock.Should().BeTrue("otherwise this test proves nothing about what flagging costs");
            stored.QuantityDispensed.Should().Be(0);
            stored.QuantityRemaining.Should().Be(21);
            stored.Status.Should().Be(RxLineStatus.Active, "the line is still dispensable the moment stock arrives");
        }
        finally { await app.CleanupAsync(); }
    }

    /// <summary>
    /// Dispensing against a flagged line CLEARS the flag.
    /// </summary>
    /// <remarks>
    /// Something has just been handed over against it, so "the counter could not fill this line" is no longer
    /// true. A chip that outlives the shortage is worse than no chip: the next pharmacist reads a stale
    /// warning and rings a prescriber about a medicine that is on the shelf.
    /// </remarks>
    [SkippableFact]
    public async Task A_dispense_clears_the_shortage()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);
            var client = Counter(app);

            await client.PostAsJsonAsync($"/api/v1/prescriptions/{rxId}/lines/{lineId}/out-of-stock",
                new { prescriptionLineId = lineId, quantity = (decimal?)null, note = "back-order" });
            (await LineAsync(lineId)).OutOfStock.Should().BeTrue();

            var dispense = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/dispense")
            {
                Content = JsonContent.Create(new
                {
                    quantity = 7m,
                    batchNo = "LOT-1",
                    expiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                }),
            };
            dispense.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var res = await client.SendAsync(dispense);
            res.StatusCode.Should().Be(HttpStatusCode.OK);

            var stored = await LineAsync(lineId);
            stored.OutOfStock.Should().BeFalse();
            // All four clear together — `out_of_stock_at`/`_by` are bound by ck_rx_line_out_of_stock_complete,
            // and a note left behind would describe a shortage that has ended.
            stored.OutOfStockBy.Should().BeNull();
            stored.OutOfStockQty.Should().BeNull();
            stored.OutOfStockNote.Should().BeNull();
            stored.QuantityDispensed.Should().Be(7m);
        }
        finally { await app.CleanupAsync(); }
    }
}
