using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 32.5 — notes on a prescription line (design 46 §7b).
/// </summary>
/// <remarks>
/// <para>
/// Doc 46 §7b is titled <b>"Notes on prescriptions, labs, radiology and procedures"</b> and opens "Every
/// order line gains notes". orders-service built it — read, write, cancel, three visibility classes,
/// sensitivity inherited, a 500-character cap with helper text saying clinical findings belong in the
/// encounter note. pharmacy-service never got any of it, so the one order kind the doc names FIRST is the
/// one kind with no notes at all: "patient cannot swallow tablets — syrup if available" had nowhere to go.
/// </para>
/// <para>
/// This is a PORT of orders' model, not a second one. The doc is explicit about why: "A second notes
/// mechanism means two behaviours for 'cancel a note' and two answers to 'who can read this'." The
/// vocabulary (<c>NoteVisibility</c>, <c>NoteReader</c>, <c>NoteAudience</c>) is shared from
/// <c>libs/amendment</c> rather than redeclared.
/// </para>
/// </remarks>
[Collection("pharmacy-db")]
public class PrescriptionNoteTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly Guid Pharmacy = new("44444444-4444-4444-4444-444444444444");

    private sealed record NoteRow(
        Guid NoteId, Guid PrescriptionLineId, string Visibility, string Body, string AuthorDisplayName,
        DateTimeOffset AuthoredAt, string Status, DateTimeOffset? CancelledAt, string? CancelReason);

    [SkippableFact]
    public async Task A_prescriber_writes_an_instruction_and_the_counter_reads_it()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);

            var written = await app.Prescriber().PostAsJsonAsync(
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes",
                new { body = "Patient cannot swallow tablets — syrup if available." });
            written.StatusCode.Should().Be(HttpStatusCode.Created);

            // An instruction nobody reads is worthless (doc 46 §7b), so the counter's read is half the test.
            var read = await Counter(app).GetAsync($"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes");
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            var notes = await read.Content.ReadFromJsonAsync<List<NoteRow>>(Web) ?? [];

            notes.Should().ContainSingle(n => n.Body.StartsWith("Patient cannot swallow", StringComparison.Ordinal)
                                              && n.Visibility == "ToFulfiller");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_Internal_note_never_reaches_the_counter()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);
            await app.Prescriber().PostAsJsonAsync(
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes",
                new { body = "Query the diagnosis with the referring clinic before this is filled.", visibility = "Internal" });

            var read = await Counter(app).GetAsync($"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes");
            var notes = await read.Content.ReadFromJsonAsync<List<NoteRow>>(Web) ?? [];

            // FILTERED BEFORE SERIALIZATION. "The screen does not show it" is not a control: the body must
            // never reach a payload the fulfiller receives.
            notes.Should().BeEmpty();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_counter_may_only_answer_back()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);

            var resp = await Counter(app).PostAsJsonAsync(
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes",
                new { body = "Take with food.", visibility = "ToFulfiller" });

            // Letting a pharmacy write ToFulfiller or Internal would put words in the prescriber's mouth on
            // a surface that reads as clinical instruction.
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var allowed = await Counter(app).PostAsJsonAsync(
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes",
                new { body = "Out of stock in 250mg — dispensed 500mg on the prescriber's standing note.", visibility = "FromFulfiller" });
            allowed.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_note_is_not_an_amendment()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);
            var before = await LineAsync(lineId);

            await app.Prescriber().PostAsJsonAsync(
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes", new { body = "Fasting is not required." });

            var after = await LineAsync(lineId);

            // Doc 46 §7b: annotating an order does not supersede it, does not create a version, and does not
            // invalidate an authorisation. Conflating the two would send every "take with food" back to the
            // approval queue.
            after.Status.Should().Be(before.Status);
            after.QuantityPrescribed.Should().Be(before.QuantityPrescribed);
            after.SupersededById.Should().Be(before.SupersededById);
            after.AmendmentReasonCode.Should().Be(before.AmendmentReasonCode);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_cancelled_note_stays_visible_struck_through_and_needs_a_reason()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);
            var created = await app.Prescriber().PostAsJsonAsync(
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes", new { body = "Dispense the syrup." });
            var note = await created.Content.ReadFromJsonAsync<NoteRow>(Web);

            var bare = await app.Prescriber().PostAsJsonAsync(
                $"/api/v1/prescriptions/notes/{note!.NoteId}/cancel", new { reason = "  " });
            bare.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
                "\"there was a note here and it was withdrawn, by X, because Z\" is information; a gap is not");

            var cancelled = await app.Prescriber().PostAsJsonAsync(
                $"/api/v1/prescriptions/notes/{note.NoteId}/cancel", new { reason = "Wrong line." });
            cancelled.StatusCode.Should().Be(HttpStatusCode.OK);

            var read = await app.Prescriber().GetAsync($"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes");
            var notes = await read.Content.ReadFromJsonAsync<List<NoteRow>>(Web) ?? [];

            // Never deleted. The note stays, struck through, with who withdrew it and why.
            notes.Should().ContainSingle(n => n.NoteId == note.NoteId && n.Status == "Cancelled"
                                              && n.CancelReason == "Wrong line.");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_note_longer_than_the_cap_is_refused_and_says_where_clinical_findings_go()
    {
        Skip.If(PrescribingApiFactory.Db is null, "PHARMACY_TEST_DB not set — DB integration test skipped.");
        await using var app = new PrescribingApiFactory();
        try
        {
            var (rxId, lineId) = await SeedAsync(app);

            var resp = await app.Prescriber().PostAsJsonAsync(
                $"/api/v1/prescriptions/{rxId}/lines/{lineId}/notes", new { body = new string('x', 501) });

            resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var problem = await resp.Content.ReadAsStringAsync();
            // A free-text box on an order attracts clinical findings, and anything written there sits outside
            // the EMR, outside the sensitivity classification, and outside the record the next clinician
            // reads. The refusal says so rather than only naming a number.
            problem.Should().Contain("encounter note");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- harness

    private static HttpClient Counter(PrescribingApiFactory app)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", "33333333-3333-3333-3333-333333333333");
        c.DefaultRequestHeaders.Add("X-Test-Role", "pharmacist");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        c.DefaultRequestHeaders.Add("X-Test-Mfa", "1");
        c.DefaultRequestHeaders.Add("X-Test-Scope", "pharmacy:read pharmacy:dispense provider:read");
        c.DefaultRequestHeaders.Add("X-Test-Provider", Pharmacy.ToString());
        c.DefaultRequestHeaders.Add("X-Test-Features", "pharmacy");
        return c;
    }

    private static async Task<PrescriptionLine> LineAsync(Guid lineId)
    {
        await using var db = PrescribingApiFactory.Ctx();
        return await db.Set<PrescriptionLine>().AsNoTracking().SingleAsync(l => l.PrescriptionLineId == lineId);
    }

    private static async Task<(Guid RxId, Guid LineId)> SeedAsync(PrescribingApiFactory app)
    {
        await using var db = PrescribingApiFactory.Ctx();
        var rxId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        db.Prescriptions.Add(new Prescription
        {
            PrescriptionId = rxId,
            TenantId = Tenant,
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
                    PrescriptionLineId = lineId, TenantId = Tenant, PrescriptionId = rxId,
                    DrugId = app.DrugA, DrugName = "Amoxicillin 500mg",
                    QuantityPrescribed = 21, QuantityDispensed = 0,
                    Status = RxLineStatus.Active, RootLineId = lineId,
                },
            ],
        });
        await db.SaveChangesAsync();
        return (rxId, lineId);
    }
}
