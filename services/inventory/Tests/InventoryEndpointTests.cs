using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Mersal.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Inventory.Tests;

/// <summary>
/// The inventory endpoints, over HTTP.
///
/// <para>These assert what lives ONLY in the handlers, and therefore had nothing proving it while the layer
/// sat at 0.0%: the required Idempotency-Key, the branch-reach refusals, the transfer atomicity, the mapping
/// from a domain outcome to a status code and problem type, and — at runtime rather than by source scan —
/// that no route will accept a beneficiary identifier.</para>
/// </summary>
[Collection("inventory-db")]
public class InventoryEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly InventoryApiFactory _f = new();
    private static readonly Guid Maadi = new("66666666-0000-0000-0000-00000000000d");
    private static readonly Guid Dokki = new("66666666-0000-0000-0000-00000000000e");
    private static readonly Guid Aswan = new("66666666-0000-0000-0000-00000000000a");

    private Guid _item;

    public async Task InitializeAsync()
    {
        if (InventoryApiFactory.Db is null) return;
        _f.PermittedBranches.Add(Maadi);
        _f.PermittedBranches.Add(Dokki);

        await using var db = _f.Ctx();
        var now = DateTimeOffset.UtcNow;
        _item = Guid.NewGuid();
        db.Items.Add(new Item
        {
            ItemId = _item, TenantId = _f.Tenant, Sku = "EP-" + Guid.NewGuid().ToString("N")[..8],
            NameEn = "Gauze", NameAr = "شاش", Category = ItemCategory.NonMedical, UnitOfMeasure = "box",
            IsBatchTracked = false, RequiresExpiry = false, Status = ItemStatus.Active,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (InventoryApiFactory.Db is not null) await _f.CleanupAsync();
    }

    /// <summary>xUnit disposes the class after <see cref="DisposeAsync"/>; the factory owns a host and a
    /// server, so it is released here rather than leaked per test class.</summary>
    public void Dispose()
    {
        _f.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void SkipWithoutDb() =>
        Skip.If(InventoryApiFactory.Db is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");

    private static HttpRequestMessage Post(string url, object body, string? idem)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idem is not null) r.Headers.Add("Idempotency-Key", idem);
        return r;
    }

    private object Movement(Guid branch, string kind, decimal qty, string? reason = null) =>
        new { branchId = branch, itemId = _item, kind, quantity = qty, reason };

    // ---- the Idempotency-Key requirement -----------------------------------------------------------------

    [SkippableFact]
    public async Task A_MOVEMENT_WITHOUT_AN_IDEMPOTENCY_KEY_IS_REFUSED()
    {
        // Required, not optional. A double-posted receipt is a phantom stock level and the ledger has no
        // UPDATE to correct it with — only a compensating movement, which leaves two rows where one belonged.
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);

        var res = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 5), idem: null));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("urn:hbmp:idempotency-required");
    }

    [SkippableFact]
    public async Task AND_REPLAYING_ONE_APPLIES_IT_ONCE()
    {
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);
        var key = "idem-" + Guid.NewGuid().ToString("N");

        var first = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 12), key));
        var second = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 12), key));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<MovementResponse>())!.Replayed.Should().BeTrue();

        await using var db = _f.Ctx();
        (await db.Movements.AsNoTracking().Where(m => m.ItemId == _item).SumAsync(m => m.Quantity))
            .Should().Be(12m, "twelve, not twenty-four");
    }

    // ---- branch reach ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_COORDINATOR_CANNOT_POST_AT_A_CLINIC_THEY_DO_NOT_RUN()
    {
        // The canonical case from design 42 §2, at the endpoint rather than at the guard: Maadi's coordinator
        // pointing a correctly-scoped token at Dokki.
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);

        var res = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Dokki, "Receipt", 5),
            "idem-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("urn:hbmp:branch-not-in-reach");
    }

    [SkippableFact]
    public async Task AND_THE_NEGATION_THE_SAME_CALL_AT_THEIR_OWN_CLINIC_SUCCEEDS()
    {
        // Without this the refusal above would be satisfied by a host that rejects everything.
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);
        var res = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 5),
            "idem-" + Guid.NewGuid().ToString("N")));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task AN_ACTIVE_BRANCH_HEADER_OUTSIDE_THE_GRANTS_IS_REFUSED_BEFORE_ANY_HANDLER_RUNS()
    {
        // THE INVARIANT: never trust the header. Aswan is not in the permitted set, so the middleware refuses
        // the request outright rather than letting a handler decide.
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Aswan);

        var res = await c.GetAsync("/api/v1/inventory/stock");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("branch-not-permitted");
    }

    [SkippableFact]
    public async Task A_CLINICS_MANAGER_READS_EVERY_BRANCH_IN_REACH_IN_ONE_CALL()
    {
        // BranchSetScoped over HTTP: no active branch, and the response covers both clinics — the behaviour
        // that lets ONE set of screens serve a coordinator and a manager.
        SkipWithoutDb();
        var coord = _f.CoordinatorClient(Maadi);
        await coord.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 7), "k1-" + Guid.NewGuid().ToString("N")));

        var mgr = _f.ManagerClient();
        var res = await mgr.GetAsync("/api/v1/inventory/stock");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<StockResponse>();
        body!.Branches.Should().BeEquivalentTo([Maadi, Dokki], "both clinics in reach, in one response");
    }

    [SkippableFact]
    public async Task A_MANAGERS_FILTER_NARROWS_THE_SAME_RESPONSE()
    {
        SkipWithoutDb();
        var res = await _f.ManagerClient(Dokki).GetAsync("/api/v1/inventory/stock");
        (await res.Content.ReadFromJsonAsync<StockResponse>())!.Branches.Should().BeEquivalentTo([Dokki]);
    }

    // ---- scope separation --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_READ_ONLY_CALLER_MAY_READ_AND_MAY_NOT_WRITE()
    {
        SkipWithoutDb();
        var c = _f.ReadOnlyClient(Maadi);

        (await c.GetAsync("/api/v1/inventory/stock")).StatusCode.Should().Be(HttpStatusCode.OK);

        var write = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 1),
            "idem-" + Guid.NewGuid().ToString("N")));
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- outcome → status mapping ------------------------------------------------------------------------

    [SkippableFact]
    public async Task ISSUING_MORE_THAN_IS_ON_HAND_IS_422_WITH_THE_BALANCE()
    {
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);
        await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 3), "r-" + Guid.NewGuid().ToString("N")));

        var res = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Issue", 10), "i-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var text = await res.Content.ReadAsStringAsync();
        text.Should().Contain("urn:hbmp:insufficient-stock");
        text.Should().Contain("3", "the response says what IS on hand, so the operator does not have to go and look");
    }

    [SkippableFact]
    public async Task AN_ADJUSTMENT_WITHOUT_A_REASON_IS_REFUSED()
    {
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);
        var res = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Adjustment", 2),
            "a-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("urn:hbmp:reason-required");
    }

    [SkippableFact]
    public async Task A_NEGATIVE_QUANTITY_IS_REFUSED_BECAUSE_THE_KIND_OWNS_THE_SIGN()
    {
        // An API that accepted "-5 for an issue" would eventually receive "+5 for an issue" from some client,
        // and the ledger would be wrong in the direction nobody checks.
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);
        var res = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Issue", -5),
            "n-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("urn:hbmp:invalid-quantity");
    }

    [SkippableFact]
    public async Task POSTING_HALF_A_TRANSFER_THROUGH_THE_MOVEMENTS_ENDPOINT_IS_REFUSED()
    {
        // A lone TransferOut would destroy stock in transit. The endpoint sends the caller to /transfers,
        // which writes both halves atomically.
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);
        var res = await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "TransferOut", 1),
            "t-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("urn:hbmp:use-transfers-endpoint");
    }

    // ---- transfers ---------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_TRANSFER_WRITES_BOTH_HALVES_AND_LEAVES_THE_NETWORK_TOTAL_UNCHANGED()
    {
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);
        await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 20), "r-" + Guid.NewGuid().ToString("N")));

        var res = await c.SendAsync(Post("/api/v1/inventory/transfers",
            new { fromBranchId = Maadi, toBranchId = Dokki, itemId = _item, quantity = 8, reason = "cover Dokki" },
            "x-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<TransferResponse>())!.NetChange.Should().Be(0m);

        await using var db = _f.Ctx();
        (await db.Movements.AsNoTracking().Where(m => m.ItemId == _item).SumAsync(m => m.Quantity))
            .Should().Be(20m, "nothing created or destroyed in transit");
    }

    [SkippableFact]
    public async Task A_TRANSFER_INTO_A_CLINIC_OUTSIDE_REACH_IS_REFUSED()
    {
        // BOTH ends are checked: a coordinator may move stock out of their own clinic and may not reach into
        // another's — the same rule in both directions.
        SkipWithoutDb();
        var res = await _f.CoordinatorClient(Maadi).SendAsync(Post("/api/v1/inventory/transfers",
            new { fromBranchId = Maadi, toBranchId = Aswan, itemId = _item, quantity = 1 },
            "y-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task A_TRANSFER_TO_THE_SAME_CLINIC_IS_REFUSED()
    {
        SkipWithoutDb();
        var res = await _f.CoordinatorClient(Maadi).SendAsync(Post("/api/v1/inventory/transfers",
            new { fromBranchId = Maadi, toBranchId = Maadi, itemId = _item, quantity = 1 },
            "z-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("urn:hbmp:invalid-transfer");
    }

    // ---- catalogue ---------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task CREATING_A_CONTROLLED_ITEM_IS_REFUSED_WITH_A_REASON()
    {
        // D1, at the endpoint: the answer is "not in this version", not a bare constraint violation.
        SkipWithoutDb();
        var res = await _f.CoordinatorClient(Maadi).SendAsync(Post("/api/v1/inventory/items", new
        {
            sku = "CTRL-" + Guid.NewGuid().ToString("N")[..6], nameEn = "Morphine", nameAr = "مورفين",
            category = "Medical", unitOfMeasure = "ampoule", isControlled = true,
        }, idem: null));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("urn:hbmp:controlled-substances-excluded");
    }

    [SkippableFact]
    public async Task A_MEDICAL_ITEM_IS_FORCED_TO_BATCH_AND_EXPIRY_TRACKING_WHATEVER_THE_REQUEST_SAYS()
    {
        // The request asks for neither; the endpoint sets both, because a medical consumable whose batch
        // nobody recorded cannot be recalled.
        SkipWithoutDb();
        var res = await _f.CoordinatorClient(Maadi).SendAsync(Post("/api/v1/inventory/items", new
        {
            sku = "MED-" + Guid.NewGuid().ToString("N")[..6], nameEn = "Sutures", nameAr = "خيوط",
            category = "Medical", unitOfMeasure = "each", isBatchTracked = false, requiresExpiry = false,
        }, idem: null));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await res.Content.ReadFromJsonAsync<ItemResponse>();
        item!.IsBatchTracked.Should().BeTrue();
        item.RequiresExpiry.Should().BeTrue();
    }

    // ---- the ledger and the alerts read ------------------------------------------------------------------

    [SkippableFact]
    public async Task THE_LEDGER_READS_BACK_WITH_ITS_SIGNS()
    {
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);
        await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Receipt", 9), "l1-" + Guid.NewGuid().ToString("N")));
        await c.SendAsync(Post("/api/v1/inventory/movements", Movement(Maadi, "Issue", 4), "l2-" + Guid.NewGuid().ToString("N")));

        var res = await c.GetAsync("/api/v1/inventory/movements?pageSize=50");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await res.Content.ReadFromJsonAsync<MovementsPage>();
        page!.Movements.Should().HaveCount(2);
        page.Movements.Sum(m => m.Quantity).Should().Be(5m, "+9 and -4 — the sign is on the row");
    }

    [SkippableFact]
    public async Task THE_ALERTS_ENDPOINT_ANSWERS_FOR_THE_BRANCHES_IN_REACH()
    {
        SkipWithoutDb();
        var res = await _f.ManagerClient().GetAsync("/api/v1/inventory/alerts");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<AlertsResponse>())!.Branches.Should().BeEquivalentTo([Maadi, Dokki]);
    }

    // ---- the boundary ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_BENEFICIARY_IDENTIFIER_IN_THE_BODY_IS_IGNORED_AND_NEVER_STORED()
    {
        // The source scan proves no CONTRACT names a person. This proves the running service does not quietly
        // accept one anyway: an extra JSON property must not reach the ledger by any route.
        SkipWithoutDb();
        var c = _f.CoordinatorClient(Maadi);

        var res = await c.SendAsync(Post("/api/v1/inventory/movements", new
        {
            branchId = Maadi, itemId = _item, kind = "Receipt", quantity = 4,
            beneficiaryId = Guid.NewGuid(), encounterId = Guid.NewGuid(), patientName = "Omar Khalil",
        }, "phi-" + Guid.NewGuid().ToString("N")));

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = _f.Ctx();
        var row = await db.Movements.AsNoTracking().FirstAsync(m => m.ItemId == _item);
        // Every column, serialised: if a person's identifier had been persisted anywhere, it would appear here.
        var serialised = System.Text.Json.JsonSerializer.Serialize(row);
        serialised.Should().NotContain("Omar", "clinic inventory carries no PHI (design 42 §7 rule 9)");
        serialised.Should().NotContain("beneficiary", "and no beneficiary identifier, by any route");
    }

    // ---- response shapes ---------------------------------------------------------------------------------

    private sealed record MovementResponse(Guid MovementId, bool Replayed, decimal Quantity, decimal OnHand);
    private sealed record TransferResponse(Guid TransferRef, decimal NetChange);
    private sealed record StockResponse(DateOnly AsOf, List<Guid> Branches);
    private sealed record AlertsResponse(DateOnly AsOf, List<Guid> Branches);
    private sealed record MovementsPage(int Total, List<LedgerRow> Movements);
    private sealed record LedgerRow(Guid MovementId, string Kind, decimal Quantity);
    private sealed record ItemResponse(Guid ItemId, string Sku, bool IsBatchTracked, bool RequiresExpiry);
}
