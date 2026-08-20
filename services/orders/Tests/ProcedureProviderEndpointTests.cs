using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mersal.Orders.Api;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Tests;

/// <summary>
/// 29.2b / design 45 §2b — the external delivering provider's portal, end to end.
///
/// <para>Two properties dominate: a centre sees only ITS OWN rows, and a double-tapped "record session" must
/// not burn two of a beneficiary's approved visits.</para>
/// </summary>
[Collection("orders-db")]
public class ProcedureProviderEndpointTests
{
    private static readonly Guid ProviderA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly Guid ProviderB = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b");

    [SkippableFact]
    public async Task Provider_A_cannot_see_provider_Bs_orders_in_the_queue()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (aOrder, _) = await SeedAsync(app, ProviderA);
            var (bOrder, _) = await SeedAsync(app, ProviderB);

            using var centreA = Centre(app, ProviderA);
            var items = await (await centreA.GetAsync("/api/v1/procedure-orders/queue"))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [];

            var ids = items.ConvertAll(i => i.GetProperty("orderId").GetGuid());
            ids.Should().Contain(aOrder, "A's own work is in A's queue");
            ids.Should().NotContain(bOrder,
                "THE test — audit R3's DispensingGate let any authenticated pharmacist browse the whole network");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Provider_A_cannot_record_a_session_against_provider_Bs_order()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (bOrder, bLine) = await SeedAsync(app, ProviderB);

            using var centreA = Centre(app, ProviderA);
            var r = await RecordSession(centreA, bOrder, bLine, Guid.NewGuid().ToString());

            // 404, not 403: a 403 confirms the order EXISTS, which to a competitor centre holding a valid
            // order number is a membership oracle answerable without being authorised for any of it.
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);

            await using var db = OrdersApiFactory.Ctx();
            (await db.Fulfillments.CountAsync(f => f.OrderLineId == bLine)).Should().Be(0, "nothing was consumed");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_replayed_session_does_not_burn_two_of_the_beneficiarys_visits()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedAsync(app, ProviderA, sessions: 6);
            using var centre = Centre(app, ProviderA);

            var key = Guid.NewGuid().ToString();
            var first = await RecordSession(centre, orderId, lineId, key);
            var replay = await RecordSession(centre, orderId, lineId, key);

            first.StatusCode.Should().Be(HttpStatusCode.OK);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);

            // The DESIGN's own words: "a double-tapped 'record session' must not burn two of a beneficiary's
            // six approved visits." Both calls answer 1-of-6, and only one fulfilment row exists.
            foreach (var r in new[] { first, replay })
            {
                var body = await r.Content.ReadFromJsonAsync<JsonElement>();
                body.GetProperty("sessionsDelivered").GetInt32().Should().Be(1);
                body.GetProperty("sessionsAuthorised").GetInt32().Should().Be(6);
                body.GetProperty("progressLabel").GetString().Should().Be("1 of 6 sessions delivered");
            }

            await using var db = OrdersApiFactory.Ctx();
            (await db.Fulfillments.CountAsync(f => f.OrderLineId == lineId)).Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Sessions_consume_one_at_a_time_and_the_remainder_stays_active()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedAsync(app, ProviderA, sessions: 3);
            using var centre = Centre(app, ProviderA);

            for (var i = 1; i <= 3; i++)
            {
                var r = await RecordSession(centre, orderId, lineId, Guid.NewGuid().ToString());
                r.StatusCode.Should().Be(HttpStatusCode.OK);
                (await r.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("sessionsDelivered").GetInt32().Should().Be(i);
            }

            // The fourth is refused by the SHARED consume rule, not by anything session-specific.
            var fourth = await RecordSession(centre, orderId, lineId, Guid.NewGuid().ToString());
            fourth.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await fourth.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("title").GetString().Should().Be("no-sessions-remaining");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_missing_idempotency_key_is_refused_rather_than_generated()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedAsync(app, ProviderA);
            using var centre = Centre(app, ProviderA);

            // Generating one server-side would make every retry a new session — the opposite of the guarantee.
            var r = await RecordSession(centre, orderId, lineId, key: null);

            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_loop_cannot_be_closed_with_an_empty_report()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedAsync(app, ProviderA);
            using var centre = Centre(app, ProviderA);

            // An empty report is an open loop wearing a closed one's clothes. An open referral loop — the
            // beneficiary was sent somewhere and nobody ever learned what happened — is the classic outpatient
            // patient-safety failure.
            foreach (var findings in new[] { "", "   " })
            {
                var r = await centre.PostAsJsonAsync(
                    $"/api/v1/procedure-orders/{orderId}/report", new CompletionReportRequest(findings));
                r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            }

            var ok = await centre.PostAsJsonAsync(
                $"/api/v1/procedure-orders/{orderId}/report",
                new CompletionReportRequest("Six sessions completed; ROM improved, discharged."));
            ok.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var db = OrdersApiFactory.Ctx();
            var order = await db.Orders.AsNoTracking().FirstAsync(o => o.OrderId == orderId);
            order.CompletionReport.Should().NotBeNullOrWhiteSpace();
            order.CompletionReportedBy.Should().NotBeNull("a report with nobody's name on it is not a report");
            order.CompletionReportedAt.Should().NotBeNull();
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task Provider_A_cannot_close_the_loop_on_provider_Bs_order()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (bOrder, _) = await SeedAsync(app, ProviderB);
            using var centreA = Centre(app, ProviderA);

            var r = await centreA.PostAsJsonAsync(
                $"/api/v1/procedure-orders/{bOrder}/report", new CompletionReportRequest("Done."));

            r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_counter_search_requires_a_second_identifier()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            using var centre = Centre(app, ProviderA);

            // A card number alone is a lookup key, not an authenticator — cards are shared and photographed.
            // Refusing it also stops the endpoint being an existence oracle for a single card number.
            var r = await centre.GetAsync("/api/v1/procedure-orders/search?cardNumber=CARD-123");

            r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("title").GetString().Should().Be("second-identifier-required");
        }
        finally { await app.CleanupAsync(); }
    }

    // ---------------------------------------------------------------------------------------------------------
    // 32.6 — what the ROW has to carry for the portal to be able to act on it.
    //
    // Every test above hands the endpoint ids it fetched from the database. That proved the endpoint and
    // nothing else: the portal only ever has the projection, and the projection did not carry the line id.
    // So the counter sent the ORDER id where a line was expected, the server answered 404, and "Record
    // session" had never once worked — beside a green suite. These tests go through the row instead.
    // ---------------------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_queue_row_names_the_line_it_is_about()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, lineId) = await SeedAsync(app, ProviderA);
            using var centre = Centre(app, ProviderA);

            var row = (await (await centre.GetAsync("/api/v1/procedure-orders/queue"))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [])
                .Single(i => i.GetProperty("orderId").GetGuid() == orderId);

            row.GetProperty("orderLineId").GetGuid().Should().Be(lineId);
            row.GetProperty("orderLineId").GetGuid().Should().NotBe(orderId,
                "the row describes one line, and the two ids are not interchangeable — substituting one for "
                + "the other is the defect this exists to catch");
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_counter_can_record_a_session_using_only_what_the_row_gave_it()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedAsync(app, ProviderA, sessions: 6);
            using var centre = Centre(app, ProviderA);

            // THE point of this test: nothing below reads the database. The ids come from the payload the
            // portal receives, exactly as the screen has them.
            var row = (await (await centre.GetAsync("/api/v1/procedure-orders/queue"))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [])
                .Single(i => i.GetProperty("orderId").GetGuid() == orderId);

            var r = await RecordSession(
                centre, row.GetProperty("orderId").GetGuid(), row.GetProperty("orderLineId").GetGuid(),
                Guid.NewGuid().ToString());

            r.StatusCode.Should().Be(HttpStatusCode.OK, "a centre works from the row, not from the datastore");
            (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("sessionsDelivered").GetInt32().Should().Be(1);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task The_counter_says_who_was_verified()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedAsync(app, ProviderA);
            await using (var db = OrdersApiFactory.Ctx())
            {
                app.DirectoryResolvesTo = await db.Orders.Where(o => o.OrderId == orderId)
                    .Select(o => o.BeneficiaryId).SingleAsync();
            }
            app.DirectoryDisclosesName = "Amal Hassan";

            using var centre = Centre(app, ProviderA);
            var rows = await (await centre.GetAsync(
                    "/api/v1/procedure-orders/search?cardNumber=CARD-123&memberNo=M-9"))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [];

            // The section is called "Verify & Deliver". It was passed a null name, so the centre had nothing
            // to check the person against but the card number it had just typed in.
            rows.Should().NotBeEmpty();
            rows[0].GetProperty("beneficiaryDisplayName").GetString().Should().Be("Amal Hassan");

            // And NOT on the queue: a centre browsing a list of refugees' names is a disclosure nobody asked
            // for. The name is the counter's, behind two identifiers.
            var queue = await (await centre.GetAsync("/api/v1/procedure-orders/queue"))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [];
            queue[0].GetProperty("beneficiaryDisplayName").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_name_the_directory_withheld_is_absent_rather_than_invented()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedAsync(app, ProviderA);
            await using (var db = OrdersApiFactory.Ctx())
            {
                app.DirectoryResolvesTo = await db.Orders.Where(o => o.OrderId == orderId)
                    .Select(o => o.BeneficiaryId).SingleAsync();
            }
            // patient-service projects per caller. A centre it withholds the name from resolves fine and
            // gets no name — which the counter must render as "withheld", never as a record without a name
            // and never as a placeholder that would verify the wrong person.
            app.DirectoryDisclosesName = null;

            using var centre = Centre(app, ProviderA);
            var rows = await (await centre.GetAsync(
                    "/api/v1/procedure-orders/search?cardNumber=CARD-123&memberNo=M-9"))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [];

            rows.Should().NotBeEmpty("the sessions are still theirs — the NAME was withheld, not the person");
            rows[0].GetProperty("beneficiaryDisplayName").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally { await app.CleanupAsync(); }
    }

    [SkippableFact]
    public async Task A_closed_loop_shows_as_closed_on_the_row()
    {
        Skip.If(OrdersApiFactory.Db is null, "ORDERS_TEST_DB not set — DB integration test skipped.");
        await using var app = new OrdersApiFactory();
        try
        {
            var (orderId, _) = await SeedAsync(app, ProviderA);
            using var centre = Centre(app, ProviderA);

            var before = (await (await centre.GetAsync("/api/v1/procedure-orders/queue"))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [])
                .Single(i => i.GetProperty("orderId").GetGuid() == orderId);
            before.GetProperty("completionReportedAt").ValueKind.Should().Be(JsonValueKind.Null,
                "the loop is open until somebody reports back");

            (await centre.PostAsJsonAsync($"/api/v1/procedure-orders/{orderId}/report",
                new CompletionReportRequest("Six sessions completed; discharged to home exercise.")))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var after = (await (await centre.GetAsync("/api/v1/procedure-orders/queue"))
                .Content.ReadFromJsonAsync<List<JsonElement>>() ?? [])
                .Single(i => i.GetProperty("orderId").GetGuid() == orderId);

            // A centre cannot be asked to close a loop it cannot see is open, and re-reporting because the
            // screen said nothing is how one episode becomes two entries in the doctor's inbox.
            after.GetProperty("completionReportedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        }
        finally { await app.CleanupAsync(); }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static HttpClient Centre(OrdersApiFactory app, Guid providerId)
    {
        var c = app.As(Guid.NewGuid().ToString(), "procedure_provider", "procedure:read procedure:consume");
        c.DefaultRequestHeaders.Add("X-Test-Provider", providerId.ToString());
        return c;
    }

    private static async Task<HttpResponseMessage> RecordSession(
        HttpClient client, Guid orderId, Guid lineId, string? key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/procedure-orders/{orderId}/sessions")
        {
            Content = JsonContent.Create(new RecordSessionRequest(lineId, "PT Nour", true, null)),
        };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(req);
    }

    /// <summary>Seed a Procedure order ROUTED TO <paramref name="providerId"/>, directly at the datastore —
    /// assignment is a routing decision, not something the centre can make for itself.</summary>
    private static async Task<(Guid OrderId, Guid LineId)> SeedAsync(
        OrdersApiFactory app, Guid providerId, decimal sessions = 6m)
    {
        await using var db = OrdersApiFactory.Ctx();
        var order = new InvestigationOrder
        {
            OrderId = Guid.NewGuid(),
            TenantId = app.Tenant,
            OrderNo = $"ORD-2026-{Random.Shared.Next(100000, 999999)}",
            BeneficiaryId = Guid.NewGuid(),
            EncounterId = Guid.NewGuid(),
            OrderingProviderId = Guid.NewGuid(),
            AssignedProviderId = providerId,
            OrderType = OrderType.Procedure,
            Status = OrderStatus.Active,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(60),
            Lines =
            [
                new OrderLine
                {
                    OrderLineId = Guid.NewGuid(),
                    TenantId = app.Tenant,
                    CodeSystem = CodeSystem.CPT, Code = "97110", Description = "Therapeutic exercise",
                    ProcedureTypeCode = "Physiotherapy",
                    RequestedQuantity = sessions, QuantityOrdered = sessions,
                    Status = OrderLineStatus.Active,
                },
            ],
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return (order.OrderId, order.Lines[0].OrderLineId);
    }
}
