using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.5b — design 46 §7b, asserted over the SERIALIZED PAYLOAD.
///
/// <para>"The screen does not show it" is not a control. A projection test that inspects a C# object proves
/// the filter ran; only reading the bytes on the wire proves nothing leaked past it — and the leak that
/// matters here is a clinician's internal reasoning reaching the external centre design 45 §2b built a
/// deliberately narrow projection for.</para>
/// </summary>
[Collection("orders-db")]
public class OrderNoteProjectionTests(OrdersApiFactory f) : IClassFixture<OrdersApiFactory>
{
    private static readonly Guid Centre = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");

    [SkippableFact]
    public async Task An_external_provider_receives_NO_TRACE_of_an_Internal_note_in_the_raw_payload()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await SeedRoutedOrder();
            await SeedNote(lineId, "Internal", "query the diagnosis with Dr Salma before booking");
            await SeedNote(lineId, "ToFulfiller", "fasting sample");
            await SeedNote(lineId, "FromFulfiller", "sample haemolysed, please repeat");

            // Read through the PROVIDER PORTAL, which is where an external centre lives (design 45 §2b) —
            // they are not inside the clinical gate, and the notes projection follows them there rather than
            // widening that gate to let them in.
            var centre = f.As("22222222-2222-2222-2222-222222222222", "procedure_provider", "procedure:read");
            centre.DefaultRequestHeaders.Add("X-Test-Provider", Centre.ToString());

            var res = await centre.GetAsync($"/api/v1/procedure-orders/{orderId}/lines/{lineId}/notes");
            res.StatusCode.Should().Be(HttpStatusCode.OK);

            // THE RAW BYTES, not a deserialized view.
            var raw = await res.Content.ReadAsStringAsync();
            raw.Should().NotContain("query the diagnosis",
                "the clinician's internal reasoning must not reach the external centre — the note would be "
                + "the gap in the projection built for them");
            raw.Should().NotContain("Internal",
                "not even the CLASS of a note they cannot read: knowing an internal note exists on this line "
                + "is itself a disclosure about the clinician's thinking");
            raw.Should().Contain("fasting sample", "the instruction meant for them must arrive");
            raw.Should().Contain("haemolysed", "and their own reply");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task An_internal_clinician_sees_every_class()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await SeedRoutedOrder();
            await SeedNote(lineId, "Internal", "query the diagnosis with Dr Salma");
            await SeedNote(lineId, "ToFulfiller", "fasting sample");

            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            var raw = await (await doctor.GetAsync(
                $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/notes")).Content.ReadAsStringAsync();

            raw.Should().Contain("query the diagnosis");
            raw.Should().Contain("fasting sample");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_note_written_on_v1_stays_visible_after_the_line_is_amended()
    {
        // The note is about the clinical INTENT, which survives an amendment. Losing it on supersede would
        // silently drop "patient cannot swallow tablets — syrup if available" the moment a dose was corrected.
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await SeedRoutedOrder(quantity: 4);
            await SeedNote(lineId, "ToFulfiller", "left knee, post-op review");

            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            doctor.DefaultRequestHeaders.Add("Idempotency-Key", $"amend-{Guid.NewGuid()}");
            (await doctor.PostAsJsonAsync($"/api/v1/investigation-orders/{orderId}/lines/{lineId}/amend",
                new { quantityOrdered = 2, reasonCode = "ClinicalChange", reasonText = (string?)null }))
                .IsSuccessStatusCode.Should().BeTrue();

            await using var db = OrdersApiFactory.Ctx();
            var successor = await db.OrderLines.AsNoTracking()
                .SingleAsync(l => l.OrderId == orderId && l.VersionNo == 2);

            var raw = await (await doctor.GetAsync(
                    $"/api/v1/investigation-orders/{orderId}/lines/{successor.OrderLineId}/notes"))
                .Content.ReadAsStringAsync();
            raw.Should().Contain("left knee",
                "the note follows the CHAIN, because what it says is still true of the corrected line");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Adding_a_note_neither_supersedes_the_line_nor_re_triggers_authorisation()
    {
        // Design 46 §7b's sharpest rule. Conflating a note with an amendment would send every "fasting
        // sample" back to the approval queue.
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await SeedRoutedOrder(gated: true);

            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            var res = await doctor.PostAsJsonAsync(
                $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/notes",
                new { body = "fasting sample", visibility = (string?)null });
            res.StatusCode.Should().Be(HttpStatusCode.Created);

            await using var db = OrdersApiFactory.Ctx();
            var line = await db.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
            line.VersionNo.Should().Be(1, "a note is an annotation, not a new version of the order");
            line.Status.Should().Be(OrderLineStatus.Active);
            line.AmendedAt.Should().BeNull();

            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);
            order.Status.Should().Be(OrderStatus.Active, "and it does not send the order back for approval");

            (await db.LineAmendments.AsNoTracking().CountAsync(a => a.OrderId == orderId))
                .Should().Be(0, "no amendment record is written for an annotation");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_note_over_the_cap_is_refused_with_the_reason_it_exists()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await SeedRoutedOrder();
            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            var res = await doctor.PostAsJsonAsync(
                $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/notes",
                new { body = new string('x', 501), visibility = (string?)null });

            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()
                .Should().Contain("encounter note",
                    "the cap exists because a free-text box attracts clinical findings, and the refusal is "
                    + "the place to say where they belong instead");
        }
        finally { await f.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Cancelling_a_note_marks_it_and_keeps_it_visible()
    {
        Skip.If(OrdersApiFactory.Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        try
        {
            var (orderId, lineId) = await SeedRoutedOrder();
            var doctor = f.As(OrdersTestAuth.DoctorSub, "doctor", "orders:read orders:write");
            var created = await (await doctor.PostAsJsonAsync(
                $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/notes",
                new { body = "fasting sample", visibility = (string?)null }))
                .Content.ReadFromJsonAsync<JsonElement>();
            var noteId = created.GetProperty("noteId").GetGuid();

            (await doctor.PostAsJsonAsync($"/api/v1/investigation-orders/notes/{noteId}/cancel",
                new { reason = "wrong line" })).IsSuccessStatusCode.Should().BeTrue();

            var raw = await (await doctor.GetAsync(
                $"/api/v1/investigation-orders/{orderId}/lines/{lineId}/notes")).Content.ReadAsStringAsync();
            raw.Should().Contain("fasting sample",
                "a cancelled note stays visible, struck through: 'there was a note here and it was withdrawn, "
                + "by X, on Y, because Z' is information; a gap is not");
            raw.Should().Contain("wrong line");

            await using var db = OrdersApiFactory.Ctx();
            (await db.OrderNotes.AsNoTracking().CountAsync(n => n.NoteId == noteId))
                .Should().Be(1, "cancellable, never deletable");
        }
        finally { await f.CleanupAsync(); }
    }

    // ---------------------------------------------------------------- harness

    private async Task<(Guid orderId, Guid lineId)> SeedRoutedOrder(decimal quantity = 1, bool gated = false)
    {
        await using var db = OrdersApiFactory.Ctx();
        var line = new OrderLine
        {
            OrderLineId = Guid.NewGuid(), TenantId = f.Tenant, CodeSystem = CodeSystem.CPT,
            Code = "80053", QuantityOrdered = quantity, RequestedQuantity = quantity,
        };
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(), TenantId = f.Tenant,
            OrderNo = await new Infrastructure.OrderNoIssuer(db).NextAsync(2026),
            BeneficiaryId = Guid.NewGuid(), EncounterId = Guid.NewGuid(), OrderingProviderId = Guid.NewGuid(),
            OrderType = OrderType.Lab, Status = OrderStatus.Active, RequestedAt = DateTimeOffset.UtcNow,
            AssignedProviderId = Centre, AuthorizationId = gated ? Guid.NewGuid() : null,
            CreatedBy = OrdersTestAuth.DoctorSub, Lines = [line],
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return (order.OrderId, line.OrderLineId);
    }

    private async Task SeedNote(Guid lineId, string visibility, string body)
    {
        await using var db = OrdersApiFactory.Ctx();
        var line = await db.OrderLines.AsNoTracking().SingleAsync(l => l.OrderLineId == lineId);
        db.OrderNotes.Add(new OrderNote
        {
            NoteId = Guid.NewGuid(), TenantId = f.Tenant, SubjectType = "OrderLine", SubjectId = lineId,
            RootLineId = line.RootLineId, Visibility = visibility, Body = body,
            AuthorUserId = Guid.NewGuid(), AuthorDisplayName = "Dr Karim",
            AuthoredAt = DateTimeOffset.UtcNow, Status = "Active",
        });
        await db.SaveChangesAsync();
    }
}
