using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Provider.Api;

/// <summary>Phase 14.5 — practitioner records, specialty & doctor↔branch assignment (design 37 §4). Writes are
/// Network/Org Admin (provider:write, audited); the picker feed is provider:read and MIN-NECESSARY (no licence
/// numbers to non-admin callers). A doctor may serve one-or-many branches; the <c>serves-branch</c> probe lets
/// emr enforce that booking/availability only happen at an assigned branch (422 otherwise).</summary>
public static class PractitionerEndpoints
{
    public static void MapPractitioners(this WebApplication app)
    {
        // The PICKER reads — the specialty reference set, the practitioner list, the serves-branch probe —
        // accept the narrow `practitioner:read` as well as the directory-wide `provider:read` (14.5 / identity
        // migration 0018). Reception and the call centre hold only the former, and without this the two
        // fields the booking screen filters on were unreadable by the people doing the booking. The
        // projection was already min-necessary: `ToView` omits the licence number for anyone without
        // `provider:write`, so widening WHO may call this does not widen WHAT comes back.
        var read = app.MapGroup("/api/v1")
            .RequireAuthorization(HbmpPolicies.AnyScope("provider:read", "practitioner:read"));
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:write"));

        // --- Reference specialties (org data) ----------------------------------------------------
        read.MapGet("/specialties", async (ProviderDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Specialties.AsNoTracking().Where(s => !s.IsDeleted).OrderBy(s => s.NameEn).ToListAsync(ct))
                .Select(s => new { s.SpecialtyCode, s.NameEn, s.NameAr, s.ParentCode })));

        // --- Create a practitioner ---------------------------------------------------------------
        write.MapPost("/practitioners", async (CreatePractitioner req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");
            if (!Enum.TryParse<PractitionerType>(req.PractitionerType, out var type))
                return Results.Problem(statusCode: 400, title: $"unknown practitioner_type '{req.PractitionerType}'");

            var now = clock.GetUtcNow();
            var p = new Practitioner
            {
                PractitionerId = Guid.NewGuid(), TenantId = tenant, UserId = req.UserId, PractitionerType = type,
                FullNameEn = req.FullNameEn, FullNameAr = req.FullNameAr, LicenseNo = req.LicenseNo,
                LicenseExpiry = req.LicenseExpiry, Status = PractitionerStatus.Active, CreatedAt = now, UpdatedAt = now,
            };
            db.Practitioners.Add(p);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "a practitioner already exists for this user"); }
            await audit.EmitAsync(Draft(p, AuditAction.Create, me, tenant, "created"), ct);
            return Results.Created($"/api/v1/practitioners/{p.PractitionerId}", await ViewAsync(db, p.PractitionerId, canSeeLicense: true, ct));
        });

        // --- Assign a specialty (one primary enforced by partial-unique index → 409) --------------
        write.MapPost("/practitioners/{id:guid}/specialties", async (Guid id, AssignSpecialty req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (!await db.Specialties.AnyAsync(s => s.SpecialtyCode == req.SpecialtyCode && !s.IsDeleted, ct))
                return Results.Problem(statusCode: 400, title: $"unknown specialty '{req.SpecialtyCode}'");

            db.PractitionerSpecialties.Add(new PractitionerSpecialty { PractitionerId = id, SpecialtyCode = req.SpecialtyCode, IsPrimary = req.IsPrimary });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "specialty already assigned or a primary specialty already exists"); }
            await audit.EmitAsync(Draft(p, AuditAction.Update, me, tenant, "specialty-assigned", req.SpecialtyCode), ct);
            return Results.Ok(new { p.PractitionerId, req.SpecialtyCode, req.IsPrimary });
        });

        // --- Revoke a specialty ------------------------------------------------------------------
        //
        // Refuses to remove the PRIMARY one. The primary specialty is what the booking screen filters on, so
        // removing it silently turns a bookable doctor into a record that appears in no picker — a change
        // nobody would connect to this action a week later when reception cannot find them. Promote a
        // different specialty first (the endpoint below), which makes the intent explicit and never leaves
        // the practitioner without one.
        write.MapPost("/practitioners/{id:guid}/specialties/revoke", async (Guid id, RevokeSpecialty req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.Include(x => x.Specialties).FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var row = p.Specialties.FirstOrDefault(s => s.SpecialtyCode == req.SpecialtyCode);
            if (row is null) return Results.Problem(statusCode: 404, title: $"'{req.SpecialtyCode}' is not assigned to this practitioner", type: "https://mersal.foundation/problems/not-found");
            if (row.IsPrimary)
                return Results.Problem(statusCode: 409, title: "primary-specialty-cannot-be-revoked",
                    type: "urn:hbmp:primary-specialty-required",
                    detail: "Promote another specialty to primary first — a practitioner without one cannot be booked.");

            db.PractitionerSpecialties.Remove(row);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(p, AuditAction.Update, me, tenant, "specialty-revoked", req.SpecialtyCode), ct);
            return Results.Ok(new { p.PractitionerId, req.SpecialtyCode });
        });

        // --- Promote a specialty to primary ------------------------------------------------------
        //
        // Two writes inside ONE transaction, in this order, because `ux_practitioner_primary_specialty` is a
        // partial-unique index over (practitioner_id) WHERE is_primary: setting the new primary before
        // clearing the old one violates it mid-transaction. Assigning the specialty when it is not yet held
        // is deliberate — "make cardiology their primary" should not fail because a separate assign step was
        // skipped.
        write.MapPost("/practitioners/{id:guid}/specialties/primary", async (Guid id, AssignSpecialty req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.Include(x => x.Specialties).FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (!await db.Specialties.AnyAsync(s => s.SpecialtyCode == req.SpecialtyCode && !s.IsDeleted, ct))
                return Results.Problem(statusCode: 400, title: $"unknown specialty '{req.SpecialtyCode}'");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var s in p.Specialties.Where(s => s.IsPrimary)) s.IsPrimary = false;
            await db.SaveChangesAsync(ct);

            var target = p.Specialties.FirstOrDefault(s => s.SpecialtyCode == req.SpecialtyCode);
            if (target is null)
                db.PractitionerSpecialties.Add(new PractitionerSpecialty { PractitionerId = id, SpecialtyCode = req.SpecialtyCode, IsPrimary = true });
            else
                target.IsPrimary = true;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await audit.EmitAsync(Draft(p, AuditAction.Update, me, tenant, "primary-specialty-set", req.SpecialtyCode), ct);
            return Results.Ok(new { p.PractitionerId, req.SpecialtyCode, IsPrimary = true });
        });

        // --- Assign a branch (a doctor may serve one-or-many) ------------------------------------
        write.MapPost("/practitioners/{id:guid}/branches", async (Guid id, AssignPractitionerBranch req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            db.PractitionerBranchAssignments.Add(new PractitionerBranchAssignment
            {
                AssignmentId = Guid.NewGuid(), PractitionerId = id, BranchId = req.BranchId,
                ValidFrom = req.ValidFrom, ValidTo = req.ValidTo, Status = "Active",
            });
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(p, AuditAction.Update, me, tenant, "branch-assigned", req.BranchId.ToString()), ct);
            return Results.Ok(new { p.PractitionerId, req.BranchId });
        });

        // --- Revoke a branch assignment ----------------------------------------------------------
        //
        // Sets status='Revoked' rather than deleting: the assignment is the record of where this clinician
        // WAS working, and an appointment booked last month at that branch is only explicable if the
        // assignment behind it still exists. The `Revoked` value has been in the CHECK constraint since 0006
        // with nothing to set it.
        //
        // This immediately makes `serves-branch` false, which is what emr's two booking gates read — so new
        // slots and new bookings at that branch are refused from here on. It does NOT touch appointments
        // ALREADY booked there; emr owns those and provider-service cannot see them. The event below exists
        // so that reconciliation can be built where it belongs (nothing consumes it yet — see the README).
        write.MapPost("/practitioners/{id:guid}/branches/revoke", async (Guid id, RevokePractitionerBranch req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var rows = await db.PractitionerBranchAssignments
                .Where(a => a.PractitionerId == id && a.BranchId == req.BranchId && a.Status == "Active").ToListAsync(ct);
            if (rows.Count == 0)
                return Results.Problem(statusCode: 404, title: "no active assignment to that branch", type: "https://mersal.foundation/problems/not-found");

            // 24.3 — revoking a practitioner's branch access is an authorization change. If the event is
            // lost, downstream consumers keep treating them as assigned to a branch they no longer serve.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var a in rows) a.Status = "Revoked";
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(Draft(p, AuditAction.Update, me, tenant, "branch-revoked", req.BranchId.ToString()), ct);
            await outbox.EnqueueAsync("PractitionerBranchRevoked", "provider.events",
                new { practitionerId = id, branchId = req.BranchId, revoked = rows.Count }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { p.PractitionerId, req.BranchId, Revoked = rows.Count });
        });

        // --- Change a practitioner's status ------------------------------------------------------
        //
        // The picker feed below returns Active practitioners only, so suspending one removes them from every
        // booking screen without deleting a record that appointments and encounters still reference.
        write.MapPost("/practitioners/{id:guid}/status", async (Guid id, ChangePractitionerStatus req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.Problem(statusCode: 400, title: "a reason is required");
            if (!Enum.TryParse<PractitionerStatus>(req.Status, out var status))
                return Results.Problem(statusCode: 400, title: $"unknown status '{req.Status}'");

            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            p.Status = status;
            p.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(p, AuditAction.StateChange, me, tenant, $"status-{status}", req.Reason), ct);
            return Results.Ok(new { p.PractitionerId, Status = p.Status.ToString() });
        });

        // --- Doctor picker: filter by branch + specialty + type; min-necessary projection --------
        read.MapGet("/practitioners", async (Guid? branchId, string? specialtyCode, string? type, ProviderDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var canSeeLicense = me.Principal?.HasScope("provider:write") ?? false;
            var q = db.Practitioners.AsNoTracking().Include(x => x.Specialties).Include(x => x.BranchAssignments)
                .Where(x => x.TenantId == tenant && !x.IsDeleted && x.Status == PractitionerStatus.Active);
            if (Enum.TryParse<PractitionerType>(type, out var t)) q = q.Where(x => x.PractitionerType == t);
            if (branchId is { } b) q = q.Where(x => x.BranchAssignments.Any(a => a.BranchId == b && a.Status == "Active"));
            if (!string.IsNullOrWhiteSpace(specialtyCode)) q = q.Where(x => x.Specialties.Any(s => s.SpecialtyCode == specialtyCode));
            var rows = await q.OrderBy(x => x.FullNameEn).Take(200).ToListAsync(ct);
            return Results.Ok(rows.Select(p => ToView(p, canSeeLicense)));
        });

        // --- serves-branch probe: emr calls this to enforce booking/availability (422 if not) -----
        read.MapGet("/practitioners/{id:guid}/serves-branch", async (Guid id, Guid branchId, ProviderDbContext db, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var today = calendar.Today();   // 18.A3
            var serves = await db.PractitionerBranchAssignments.AsNoTracking().AnyAsync(a =>
                a.PractitionerId == id && a.BranchId == branchId && a.Status == "Active"
                && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo >= today), ct);
            return Results.Ok(new { practitionerId = id, branchId, servesBranch = serves });
        });
    }

    private static PractitionerView ToView(Practitioner p, bool canSeeLicense) => new(
        p.PractitionerId, p.PractitionerType.ToString(), p.FullNameEn, p.FullNameAr,
        p.Specialties.FirstOrDefault(s => s.IsPrimary)?.SpecialtyCode,
        p.Specialties.Select(s => s.SpecialtyCode).ToList(),
        p.BranchAssignments.Where(a => a.Status == "Active").Select(a => a.BranchId).ToList(),
        p.Status.ToString(), canSeeLicense ? p.LicenseNo : null);

    private static async Task<PractitionerView> ViewAsync(ProviderDbContext db, Guid id, bool canSeeLicense, CancellationToken ct)
    {
        var p = await db.Practitioners.AsNoTracking().Include(x => x.Specialties).Include(x => x.BranchAssignments)
            .SingleAsync(x => x.PractitionerId == id, ct);
        return ToView(p, canSeeLicense);
    }

    private static AuditEventDraft Draft(Practitioner p, AuditAction action, IHbmpPrincipalAccessor me, string? tenant, string? outcome = null, string? reason = null) => new()
    {
        EntityType = "practitioner", EntityId = p.PractitionerId.ToString(), Action = action,
        ActorUserId = me.Principal?.Subject, TenantId = tenant, DecisionOutcome = outcome, DecisionReasonCode = reason,
    };
}
