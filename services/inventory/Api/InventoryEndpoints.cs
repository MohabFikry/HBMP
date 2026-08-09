using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Mersal.Inventory.Domain;
using Mersal.Inventory.Infrastructure;
using Mersal.Time;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Inventory.Api;

/// <summary>
/// 25.6 (design 42 §5/§6) — the clinic stock surface.
///
/// <para><b>ONE set of endpoints for both branch roles.</b> A coordinator sees their own clinic; a clinics
/// manager sees all six in one response. That falls out of <c>BranchSetScoped</c> (25.1) rather than needing
/// a separate "manager" route — separate routes would be two implementations of one rule, and the one that
/// gets forgotten is always the narrower one.</para>
///
/// <para><b>No endpoint here accepts a beneficiary identifier.</b> Not as a parameter, not in a body, not
/// "temporarily". Clinic inventory is not a second dispensing path: anything requiring a prescription goes
/// through pharmacy-service against an Rx. <c>NoPhiInInventoryTests</c> asserts it over this file and the
/// schema, so the boundary cannot erode by someone adding "just an optional encounter id".</para>
/// </summary>
public static class InventoryEndpoints
{
    public static void MapInventory(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var read = app.MapGroup("/api/v1/inventory")
            .RequireAuthorization(HbmpPolicies.AnyScope("branch:inventory:read", "branch:inventory:write"));
        var write = app.MapGroup("/api/v1/inventory")
            .RequireAuthorization(HbmpPolicies.Scope("branch:inventory:write"));

        // ---- catalogue ------------------------------------------------------------------------------
        //
        // The catalogue is NETWORK-WIDE reference data, not branch stock: the same syringe is the same
        // syringe at Aswan and at Dokki, and per-branch catalogues are how one item becomes six with
        // different spellings. Per-branch policy (reorder level, lead time) lives on branch_item.

        read.MapGet("/items", async (string? category, bool? includeDiscontinued, InventoryDbContext db, CancellationToken ct) =>
        {
            var q = db.Items.AsNoTracking().AsQueryable();
            if (Enum.TryParse<ItemCategory>(category, ignoreCase: true, out var c)) q = q.Where(i => i.Category == c);
            if (includeDiscontinued != true) q = q.Where(i => i.Status == ItemStatus.Active);
            var rows = await q.OrderBy(i => i.NameEn).Take(1000).ToListAsync(ct);
            return Results.Ok(rows.Select(ItemView.From));
        });

        write.MapPost("/items", async (CreateItemRequest req, InventoryDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, [FromServices] IMedicinesDirectory medicines, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");
            if (!Enum.TryParse<ItemCategory>(req.Category, out var category))
                return Results.Problem(statusCode: 400, title: $"unknown category '{req.Category}'", type: "urn:hbmp:invalid-category");

            // These four are declared non-nullable on the record, which stops nothing: a body with
            // `"sku": null` deserializes to null anyway and the .Trim() below was an unhandled 500 waiting
            // for the first client that sent one. Checked here, where the answer is a 400 that says which
            // field. NameAr is required with the rest — a catalogue that is bilingual for most items and not
            // for some is one an Arabic-speaking storekeeper cannot search.
            if (string.IsNullOrWhiteSpace(req.Sku) || string.IsNullOrWhiteSpace(req.NameEn)
                || string.IsNullOrWhiteSpace(req.NameAr) || string.IsNullOrWhiteSpace(req.UnitOfMeasure))
                return Results.Problem(statusCode: 400, type: "urn:hbmp:missing-field",
                    title: "sku, nameEn, nameAr and unitOfMeasure are all required");

            // D1 — controlled substances are excluded from v1 by a CHECK constraint. Refused here too, with a
            // reason, so the answer is "not in this version" rather than a bare constraint violation.
            if (req.IsControlled == true)
                return Results.Problem(statusCode: 422, title: "controlled-substances-not-supported",
                    type: "urn:hbmp:controlled-substances-excluded",
                    detail: "Controlled substances are out of scope for this version (ADR-0029, D1). A controlled " +
                            "register needs dual signature, a per-ampoule running balance and regulator reporting — " +
                            "a module of its own. Enabling it is a deliberate migration, not a setting.");

            // D2 — nothing patient-shaped is sent. The classify call carries a SKU and a name, which are
            // reference data about a THING, and gets back a verdict about that thing. This does not put
            // inventory on the wrong side of the PHI boundary and must not be allowed to grow into doing so.
            var verdict = await medicines.ClassifyAsync(req.Sku.Trim(), req.NameEn.Trim(), req.NameAr.Trim(), ct);

            // D5 — a medicine is PHARMACY stock, and this is the check that makes that a rule the platform
            // keeps rather than one people remember. Deliberately NOT overridable: an override flag is how a
            // classification decision that the pack says must be made once, centrally, becomes six clinics
            // each ticking a box on a Tuesday. If a genuine consumable is refused, the fix is the medicines
            // master, and that fix is visible to the people accountable for it.
            if (verdict.Verdict == MedicineVerdict.IsAMedicine)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "inventory_item", EntityId = req.Sku.Trim(), Action = AuditAction.Create,
                    ActorUserId = me.Principal?.Subject, TenantId = tenant,
                    DecisionOutcome = verdict.IsVaccine ? "RefusedVaccine" : "RefusedMedicine",
                }, ct);

                return Results.Problem(statusCode: 422,
                    title: verdict.IsVaccine ? "vaccines-are-pharmacy-stock" : "medicines-are-pharmacy-stock",
                    type: MedicineCheck.MedicineProblemType,
                    detail: $"'{verdict.DrugName}' ({verdict.DrugCode}) is in the medicines master, so it is " +
                            "pharmacy stock and not clinic stock (ADR-0029, D5). Anything dispensed against a " +
                            "prescription goes through pharmacy-service, where eligibility, coverage limits, the " +
                            "approved-medicines list and the dispensing record are enforced; admitting it here " +
                            "would be a second dispensing route around all four. If this is genuinely a " +
                            "consumable, the medicines master is what needs correcting — not this item.");
            }

            // Fail-closed. See MedicineVerdict.DirectoryUnreachable for why this is the easy call: the cost is
            // that a new gauze SKU waits, and the alternative is an open gate exactly when nobody is looking.
            if (verdict.Verdict == MedicineVerdict.DirectoryUnreachable)
                return Results.Problem(statusCode: 503, title: "medicines-directory-unavailable",
                    type: MedicineCheck.UnavailableProblemType,
                    detail: "Cannot confirm this item is not a medicine because the medicines master could not " +
                            "be reached. Item creation is refused until it can (ADR-0029, D5). Retry shortly.");

            // MEDICAL ⇒ batch- and expiry-tracked. Forced rather than trusted from the request: a medical
            // consumable whose batch nobody recorded cannot be recalled. The DB CHECK backs this up.
            var medical = category == ItemCategory.Medical;
            var now = clock.GetUtcNow();
            var item = new Item
            {
                ItemId = Guid.NewGuid(), TenantId = tenant, Sku = req.Sku.Trim(),
                NameEn = req.NameEn.Trim(), NameAr = req.NameAr.Trim(),
                Category = category, UnitOfMeasure = req.UnitOfMeasure.Trim(),
                IsBatchTracked = medical || req.IsBatchTracked == true,
                RequiresExpiry = medical || req.RequiresExpiry == true,
                IsControlled = false,
                StorageCondition = req.StorageCondition, ColdChain = req.ColdChain ?? false,
                Status = ItemStatus.Active,
                CreatedAt = now, CreatedBy = me.Principal?.Subject, UpdatedAt = now, UpdatedBy = me.Principal?.Subject,
            };
            db.Items.Add(item);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "an item with that SKU already exists", type: "urn:hbmp:item-exists"); }

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "inventory_item", EntityId = item.ItemId.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, TenantId = tenant, DecisionOutcome = category.ToString(),
            }, ct);
            return Results.Created($"/api/v1/inventory/items/{item.ItemId}", ItemView.From(item));
        })
        .Produces<ItemView>();

        // ---- stock: the DERIVED balance ---------------------------------------------------------------

        read.MapGet("/stock", async (
            Guid? branchId, string? category, bool? lowStock, int? expiringWithinDays,
            [FromServices] BranchReachGuard reach, InventoryDbContext db, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var branches = reach.ReadableBranches(branchId).ToList();
            var today = calendar.Today();

            // ON-HAND IS SUMMED FROM THE LEDGER. There is no quantity column to read instead, here or anywhere.
            var lines = await db.Movements.AsNoTracking()
                .Where(m => branches.Contains(m.BranchId))
                .GroupBy(m => new { m.BranchId, m.ItemId, m.BatchId })
                .Select(g => new { g.Key.BranchId, g.Key.ItemId, g.Key.BatchId, OnHand = g.Sum(x => x.Quantity) })
                .ToListAsync(ct);

            var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
            var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ItemId)).ToDictionaryAsync(i => i.ItemId, ct);
            var batchIds = lines.Where(l => l.BatchId != null).Select(l => l.BatchId!.Value).Distinct().ToList();
            var batches = await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.BatchId)).ToDictionaryAsync(b => b.BatchId, ct);
            var policies = await db.BranchItems.AsNoTracking()
                .Where(p => branches.Contains(p.BranchId)).ToListAsync(ct);

            var view = lines
                .Where(l => items.ContainsKey(l.ItemId))
                .Select(l =>
                {
                    var item = items[l.ItemId];
                    var batch = l.BatchId is { } b && batches.TryGetValue(b, out var bt) ? bt : null;
                    var policy = policies.FirstOrDefault(p => p.BranchId == l.BranchId && p.ItemId == l.ItemId);
                    return new
                    {
                        l.BranchId, l.ItemId, item.Sku, item.NameEn, item.NameAr,
                        Category = item.Category.ToString(), item.UnitOfMeasure, item.ColdChain,
                        l.BatchId, BatchNo = batch?.BatchNo, ExpiryDate = batch?.ExpiryDate,
                        OnHand = l.OnHand,
                        ReorderLevel = policy?.ReorderLevel ?? 0m,
                        IsLow = policy is not null && l.OnHand <= policy.ReorderLevel,
                        // QUARANTINED, not merely "expired": the word on the screen has to say that it cannot
                        // be issued and can only leave by a write-off.
                        IsQuarantined = StockRules.IsBatchExpired(batch?.ExpiryDate, today),
                    };
                })
                .Where(v => v.OnHand != 0 || v.IsLow)
                .ToList();

            if (Enum.TryParse<ItemCategory>(category, ignoreCase: true, out var cat))
                view = [.. view.Where(v => v.Category == cat.ToString())];
            if (lowStock == true) view = [.. view.Where(v => v.IsLow)];
            if (expiringWithinDays is { } days)
                view = [.. view.Where(v => StockRules.IsExpiringWithin(v.ExpiryDate, today, days))];

            return Results.Ok(new { asOf = today, branches, stock = view.OrderBy(v => v.NameEn) });
        });

        // ---- movements: the ledger --------------------------------------------------------------------

        write.MapPost("/movements", async (
            PostMovementRequest req, HttpRequest http, MovementService movements, BranchReachGuard reach,
            IAuditClient audit, IHbmpPrincipalAccessor me, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            // IDEMPOTENCY-KEY IS REQUIRED, not optional. A double-posted receipt is a phantom stock level, and
            // the ledger has no UPDATE to correct it with — only a compensating movement, which leaves two
            // rows where one belonged. Stable per INTENT, never per attempt.
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required",
                    type: "urn:hbmp:idempotency-required",
                    detail: "A movement without one can be double-posted by a retry, and the ledger is append-only.");

            if (!Enum.TryParse<MovementKind>(req.Kind, out var kind))
                return Results.Problem(statusCode: 400, title: $"unknown movement kind '{req.Kind}'", type: "urn:hbmp:invalid-movement-kind");
            if (kind is MovementKind.TransferIn or MovementKind.TransferOut)
                return Results.Problem(statusCode: 400, title: "use POST /inventory/transfers for a transfer",
                    type: "urn:hbmp:use-transfers-endpoint",
                    detail: "A transfer is TWO paired movements written atomically; posting one half alone would " +
                            "create or destroy stock in transit.");
            if (req.Quantity <= 0)
                return Results.Problem(statusCode: 400, title: "quantity must be positive",
                    type: "urn:hbmp:invalid-quantity",
                    detail: "Send a positive magnitude and a kind; the sign is the ledger's business, not the caller's.");

            if (await reach.RefuseUnlessInReachAsync(req.BranchId, "stock_movement", req.ItemId.ToString(), ct) is { } denied)
                return denied;

            var result = await movements.PostAsync(
                req.BranchId, req.ItemId, req.BatchId, kind, req.Quantity, req.Reason,
                me.Principal?.Subject ?? "unknown", idem, calendar.Today(), ct: ct);

            var problem = MapFailure(result);
            if (problem is not null) return problem;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "stock_movement", EntityId = result.Movement!.MovementId.ToString(),
                Action = result.Outcome == PostOutcome.Replayed ? AuditAction.Read : AuditAction.Create,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                DecisionOutcome = kind.ToString(), DecisionReasonCode = req.Reason,
            }, ct);

            return Results.Ok(new
            {
                movementId = result.Movement.MovementId,
                replayed = result.Outcome == PostOutcome.Replayed,
                quantity = result.Movement.Quantity,
                onHand = result.OnHandAfter,
            });
        });

        read.MapGet("/movements", async (
            Guid? branchId, Guid? itemId, string? kind, DateTimeOffset? from, DateTimeOffset? to,
            int? page, int? pageSize,
            [FromServices] BranchReachGuard reach, InventoryDbContext db, CancellationToken ct) =>
        {
            var branches = reach.ReadableBranches(branchId).ToList();
            var q = db.Movements.AsNoTracking().Where(m => branches.Contains(m.BranchId));
            if (itemId is { } i) q = q.Where(m => m.ItemId == i);
            if (Enum.TryParse<MovementKind>(kind, ignoreCase: true, out var k)) q = q.Where(m => m.Kind == k);
            if (from is { } lo) q = q.Where(m => m.OccurredAt >= lo);
            if (to is { } hi) q = q.Where(m => m.OccurredAt < hi);

            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var skip = Math.Max(0, (page ?? 1) - 1) * size;
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(m => m.OccurredAt).Skip(skip).Take(size).ToListAsync(ct);

            return Results.Ok(new { total, page = page ?? 1, pageSize = size, movements = rows.Select(MovementView.From) });
        });

        // ---- transfers: two paired movements, atomically ----------------------------------------------

        write.MapPost("/transfers", async (
            TransferRequest req, HttpRequest http, MovementService movements, BranchReachGuard reach,
            InventoryDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, IBusinessCalendar calendar,
            CancellationToken ct) =>
        {
            var idem = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(idem))
                return Results.Problem(statusCode: 400, title: "Idempotency-Key header is required", type: "urn:hbmp:idempotency-required");
            if (req.Quantity <= 0)
                return Results.Problem(statusCode: 400, title: "quantity must be positive", type: "urn:hbmp:invalid-quantity");
            if (req.FromBranchId == req.ToBranchId)
                return Results.Problem(statusCode: 400, title: "a transfer needs two different branches", type: "urn:hbmp:invalid-transfer");

            // BOTH ends are checked, with DIFFERENT rules, because they mean different things.
            //
            // The SOURCE is where you are working: stock leaves a shelf you are standing at, so the
            // acting-in rule applies and a branch-scoped caller must have it as their active branch.
            //
            // The DESTINATION is a counterparty. You are sending stock to a clinic you are responsible for,
            // not working in it — and you cannot be working in two clinics at once. Checking it with the
            // acting-in rule made every coordinator-initiated transfer a 403, since a transfer's two branches
            // are by definition different and only one can be active. Membership in the permitted set is the
            // right bar for the far end, and still refuses a transfer into a clinic the caller has no grant
            // for, which is the property that matters.
            if (await reach.RefuseUnlessInReachAsync(req.FromBranchId, "stock_transfer", req.ItemId.ToString(), ct) is { } d1) return d1;
            if (await reach.RefuseUnlessPermittedAsync(req.ToBranchId, "stock_transfer", req.ItemId.ToString(), ct) is { } d2) return d2;

            var transferRef = Guid.NewGuid();

            // ATOMIC. Both halves in one transaction, so nothing is created or destroyed in transit — a
            // crash between them would otherwise leave stock that had left one clinic and never arrived at
            // the other, and the ledger has no correction that restores the truth without a story.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var outbound = await movements.PostAsync(
                req.FromBranchId, req.ItemId, req.BatchId, MovementKind.TransferOut, req.Quantity,
                req.Reason, me.Principal?.Subject ?? "unknown", $"{idem}:out", calendar.Today(),
                transferRef, req.ToBranchId, ct);
            if (MapFailure(outbound) is { } outFailed) { await tx.RollbackAsync(ct); return outFailed; }

            var inbound = await movements.PostAsync(
                req.ToBranchId, req.ItemId, req.BatchId, MovementKind.TransferIn, req.Quantity,
                req.Reason, me.Principal?.Subject ?? "unknown", $"{idem}:in", calendar.Today(),
                transferRef, req.FromBranchId, ct);
            if (MapFailure(inbound) is { } inFailed) { await tx.RollbackAsync(ct); return inFailed; }

            await tx.CommitAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "stock_transfer", EntityId = transferRef.ToString(), Action = AuditAction.Create,
                ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
                DecisionOutcome = "Transferred", DecisionReasonCode = req.Reason,
            }, ct);

            return Results.Ok(new
            {
                transferRef,
                outMovementId = outbound.Movement!.MovementId,
                inMovementId = inbound.Movement!.MovementId,
                // Stated in the response because it is the invariant: nothing is created or destroyed.
                netChange = outbound.Movement.Quantity + inbound.Movement.Quantity,
            });
        });

        // ---- alerts worklist ---------------------------------------------------------------------------

        read.MapGet("/alerts", async (
            Guid? branchId, [FromServices] BranchReachGuard reach, InventoryDbContext db, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var branches = reach.ReadableBranches(branchId).ToList();
            var today = calendar.Today();

            var lines = await db.Movements.AsNoTracking()
                .Where(m => branches.Contains(m.BranchId))
                .GroupBy(m => new { m.BranchId, m.ItemId, m.BatchId })
                .Select(g => new { g.Key.BranchId, g.Key.ItemId, g.Key.BatchId, OnHand = g.Sum(x => x.Quantity) })
                .ToListAsync(ct);

            var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
            var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ItemId)).ToDictionaryAsync(i => i.ItemId, ct);
            var batchIds = lines.Where(l => l.BatchId != null).Select(l => l.BatchId!.Value).Distinct().ToList();
            var batches = await db.Batches.AsNoTracking().Where(b => batchIds.Contains(b.BatchId)).ToDictionaryAsync(b => b.BatchId, ct);
            var policies = await db.BranchItems.AsNoTracking().Where(p => branches.Contains(p.BranchId)).ToListAsync(ct);

            var lowStock = lines
                .GroupBy(l => new { l.BranchId, l.ItemId })
                .Select(g => new { g.Key.BranchId, g.Key.ItemId, OnHand = g.Sum(x => x.OnHand) })
                .Select(g => new
                {
                    g.BranchId, g.ItemId, g.OnHand,
                    Policy = policies.FirstOrDefault(p => p.BranchId == g.BranchId && p.ItemId == g.ItemId),
                })
                .Where(g => g.Policy is not null && g.OnHand <= g.Policy.ReorderLevel)
                .Select(g => new
                {
                    g.BranchId, g.ItemId,
                    Name = items.TryGetValue(g.ItemId, out var it) ? it.NameEn : "(unknown)",
                    g.OnHand, g.Policy!.ReorderLevel, g.Policy.LeadTimeDays,
                })
                .ToList();

            // Expiring stock, bucketed on the SAME 90/60/30 cadence as the licence sweeper — a coordinator
            // learns one rhythm rather than two.
            var expiring = lines
                .Where(l => l.OnHand > 0 && l.BatchId is not null && batches.ContainsKey(l.BatchId.Value))
                .Select(l => new { Line = l, Batch = batches[l.BatchId!.Value] })
                .Where(x => x.Batch.ExpiryDate is not null)
                .Select(x => new
                {
                    x.Line.BranchId, x.Line.ItemId, x.Line.BatchId, x.Batch.BatchNo, x.Batch.ExpiryDate,
                    Name = items.TryGetValue(x.Line.ItemId, out var it) ? it.NameEn : "(unknown)",
                    x.Line.OnHand,
                    DaysRemaining = x.Batch.ExpiryDate!.Value.DayNumber - today.DayNumber,
                    Quarantined = StockRules.IsBatchExpired(x.Batch.ExpiryDate, today),
                })
                .Where(x => x.DaysRemaining <= StockRules.ExpiryWarningDays.Max())
                .OrderBy(x => x.ExpiryDate)
                .ToList();

            return Results.Ok(new
            {
                asOf = today,
                branches,
                lowStock,
                expiring = expiring.Where(e => !e.Quarantined),
                // Listed separately, not merged into "expiring": expired stock is a different action. It
                // cannot be issued and leaves only by an explicit write-off with a reason.
                quarantined = expiring.Where(e => e.Quarantined),
            });
        });
    }

    /// <summary>Map a failed post onto its RFC 7807 problem. One place, so the movement and transfer endpoints
    /// cannot answer differently for the same cause.</summary>
    private static IResult? MapFailure(PostResult r) => r.Outcome switch
    {
        PostOutcome.Posted or PostOutcome.Replayed => null,
        PostOutcome.ItemNotFound => Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"),
        PostOutcome.BatchNotFound => Results.Problem(statusCode: 404, title: "batch not found", type: "https://mersal.foundation/problems/not-found"),
        PostOutcome.ReasonRequired => Results.Problem(statusCode: 400, title: "a reason is required",
            type: StockRules.ReasonRequiredProblemType,
            detail: "Adjustments, write-offs and stock-take variances record that the books were wrong. " +
                    "Without a reason the ledger stops being evidence."),
        PostOutcome.BatchRequired => Results.Problem(statusCode: 400, title: "a batch is required",
            type: StockRules.BatchRequiredProblemType,
            detail: "This item is batch-tracked: without a batch a recall cannot be scoped to the affected lot."),
        PostOutcome.BatchExpired => Results.Problem(statusCode: 422, title: "batch-expired",
            type: StockRules.BatchExpiredProblemType,
            detail: "This batch has expired and is quarantined. It cannot be issued; clear it with an explicit " +
                    "write-off recording why."),
        PostOutcome.InsufficientStock => Results.Problem(statusCode: 422, title: "insufficient-stock",
            type: StockRules.InsufficientStockProblemType,
            detail: $"On hand is {r.OnHandBefore}. Stock cannot go negative — a balance that can is a balance " +
                    "nobody can reconcile.",
            extensions: new Dictionary<string, object?> { ["onHand"] = r.OnHandBefore }),
        _ => Results.Problem(statusCode: 500, title: "unexpected movement outcome"),
    };
}

// ---- contracts. NOTE WHAT IS ABSENT: no beneficiaryId, no patientId, no encounterId, no prescriptionId.
// Clinic inventory never dispenses to a patient (design 42 §7 rule 8) and carries no PHI (rule 9).

public sealed record CreateItemRequest(
    string Sku, string NameEn, string NameAr, string Category, string UnitOfMeasure,
    bool? IsBatchTracked = null, bool? RequiresExpiry = null, bool? IsControlled = null,
    string? StorageCondition = null, bool? ColdChain = null);

public sealed record PostMovementRequest(
    Guid BranchId, Guid ItemId, string Kind, decimal Quantity,
    Guid? BatchId = null, string? Reason = null);

public sealed record TransferRequest(
    Guid FromBranchId, Guid ToBranchId, Guid ItemId, decimal Quantity,
    Guid? BatchId = null, string? Reason = null);

public sealed record ItemView(
    Guid ItemId, string Sku, string NameEn, string NameAr, string Category, string UnitOfMeasure,
    bool IsBatchTracked, bool RequiresExpiry, string? StorageCondition, bool ColdChain, string Status)
{
    public static ItemView From(Item i)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(i.ItemId, i.Sku, i.NameEn, i.NameAr, i.Category.ToString(), i.UnitOfMeasure,
            i.IsBatchTracked, i.RequiresExpiry, i.StorageCondition, i.ColdChain, i.Status.ToString());
    }
}

public sealed record MovementView(
    Guid MovementId, Guid BranchId, Guid ItemId, Guid? BatchId, string Kind, decimal Quantity,
    string? Reason, Guid? TransferRef, Guid? CounterpartyBranchId, string Actor, DateTimeOffset OccurredAt)
{
    public static MovementView From(StockMovement m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(m.MovementId, m.BranchId, m.ItemId, m.BatchId, m.Kind.ToString(), m.Quantity,
            m.Reason, m.TransferRef, m.CounterpartyBranchId, m.Actor, m.OccurredAt);
    }
}
