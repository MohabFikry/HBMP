using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Api;

/// <summary>Phase 14.1 — the internal Mersal branch registry (37 §2). Branches are org reference data with
/// NO PHI, so reads are open to any authenticated user (they drive the branch switcher and downstream
/// branch-scoping in 14.2+); writes are restricted to the Network Team / Org Admin (provider:write) and
/// every mutation is audited and emits a domain event. Branch ≠ provider_location — see Domain/Branch.cs.</summary>
public static class BranchEndpoints
{
    public static void MapBranches(this WebApplication app)
    {
        // Reads: any authenticated user (org reference data, no PHI).
        var read = app.MapGroup("/api/v1/branches").RequireAuthorization();
        // Writes: Network Team / Org Admin (validated at the gateway AND here via the provider:write scope).
        var write = app.MapGroup("/api/v1/branches").RequireAuthorization(HbmpPolicies.Scope("provider:write"));

        read.MapGet("", async (ProviderDbContext db, string? status, CancellationToken ct) =>
        {
            var q = db.Branches.AsNoTracking().Where(b => !b.IsDeleted);
            if (status is not null && Enum.TryParse<BranchStatus>(status, out var s)) q = q.Where(b => b.Status == s);
            var rows = await q.OrderBy(b => b.BranchCode).ToListAsync(ct);
            return Results.Ok(rows.Select(ToView));
        });

        read.MapGet("/{id:guid}", async (Guid id, ProviderDbContext db, CancellationToken ct) =>
        {
            var b = await db.Branches.AsNoTracking().FirstOrDefaultAsync(x => x.BranchId == id && !x.IsDeleted, ct);
            return b is null ? Results.NotFound() : Results.Ok(ToView(b));
        });

        // --- Create branch → BranchCreated -------------------------------------------------------
        write.MapPost("", async (CreateBranch req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.BranchCode) || req.BranchCode.Length > 8)
                return Results.Problem(statusCode: 400, title: "branch_code is required (max 8 chars)");
            if (string.IsNullOrWhiteSpace(req.NameEn) || string.IsNullOrWhiteSpace(req.NameAr))
                return Results.Problem(statusCode: 400, title: "name_en and name_ar are both required");

            var now = clock.GetUtcNow();
            var branch = new Branch
            {
                BranchId = Guid.NewGuid(), BranchCode = req.BranchCode.Trim().ToUpperInvariant(),
                NameEn = req.NameEn, NameAr = req.NameAr, City = req.City, Address = req.Address,
                Timezone = string.IsNullOrWhiteSpace(req.Timezone) ? "Africa/Cairo" : req.Timezone!,
                Phone = req.Phone, OpeningHours = req.OpeningHours, Status = BranchStatus.Active,
                CreatedBy = me.Principal?.Subject, UpdatedBy = me.Principal?.Subject, CreatedAt = now, UpdatedAt = now,
            };
            db.Branches.Add(branch);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: $"a branch with code '{branch.BranchCode}' already exists"); }

            await audit.EmitAsync(Draft(branch, AuditAction.Create, me, outcome: "created"), ct);
            await outbox.EnqueueAsync("BranchCreated", "provider.events", new { branchId = branch.BranchId, branch.BranchCode, branch.NameEn, branch.NameAr }, ct);
            return Results.Created($"/api/v1/branches/{branch.BranchId}", ToView(branch));
        });

        // --- Update branch metadata → BranchUpdated ----------------------------------------------
        write.MapPut("/{id:guid}", async (Guid id, UpdateBranch req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var b = await db.Branches.FirstOrDefaultAsync(x => x.BranchId == id && !x.IsDeleted, ct);
            if (b is null) return Results.NotFound();

            if (req.NameEn is not null) b.NameEn = req.NameEn;
            if (req.NameAr is not null) b.NameAr = req.NameAr;
            if (req.City is not null) b.City = req.City;
            if (req.Address is not null) b.Address = req.Address;
            if (!string.IsNullOrWhiteSpace(req.Timezone)) b.Timezone = req.Timezone!;
            if (req.Phone is not null) b.Phone = req.Phone;
            if (req.OpeningHours is not null) b.OpeningHours = req.OpeningHours;
            b.UpdatedBy = me.Principal?.Subject;
            b.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(Draft(b, AuditAction.Update, me, outcome: "updated"), ct);
            await outbox.EnqueueAsync("BranchUpdated", "provider.events", new { branchId = b.BranchId, b.BranchCode }, ct);
            return Results.Ok(ToView(b));
        });

        // --- Change status (Active/Suspended/Closed) → BranchStatusChanged -----------------------
        write.MapPost("/{id:guid}/status", async (Guid id, ChangeBranchStatus req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.Problem(statusCode: 400, title: "a reason is required");
            if (!Enum.TryParse<BranchStatus>(req.Status, out var status))
                return Results.Problem(statusCode: 400, title: $"unknown status '{req.Status}'");
            var b = await db.Branches.FirstOrDefaultAsync(x => x.BranchId == id && !x.IsDeleted, ct);
            if (b is null) return Results.NotFound();

            var from = b.Status;
            b.Status = status;
            b.UpdatedBy = me.Principal?.Subject;
            b.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(Draft(b, AuditAction.StateChange, me, outcome: status.ToString(), reason: req.Reason), ct);
            await outbox.EnqueueAsync("BranchStatusChanged", "provider.events", new { branchId = b.BranchId, b.BranchCode, from = from.ToString(), to = status.ToString() }, ct);
            return Results.Ok(new { b.BranchId, status = b.Status.ToString() });
        });
    }

    private static BranchView ToView(Branch b) => new(
        b.BranchId, b.BranchCode, b.NameEn, b.NameAr, b.City, b.Address, b.Timezone, b.Phone, b.OpeningHours, b.Status.ToString());

    private static AuditEventDraft Draft(Branch b, AuditAction action, IHbmpPrincipalAccessor me, string? outcome = null, string? reason = null) => new()
    {
        EntityType = "branch", EntityId = b.BranchId.ToString(), Action = action,
        ActorUserId = me.Principal?.Subject, TenantId = me.Principal?.TenantId,
        DecisionOutcome = outcome, DecisionReasonCode = reason,
    };
}
