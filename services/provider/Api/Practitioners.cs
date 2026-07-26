using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Api;

/// <summary>Phase 14.5 — practitioner records, specialty & doctor↔branch assignment (design 37 §4). Writes are
/// Network/Org Admin (provider:write, audited); the picker feed is provider:read and MIN-NECESSARY (no licence
/// numbers to non-admin callers). A doctor may serve one-or-many branches; the <c>serves-branch</c> probe lets
/// emr enforce that booking/availability only happen at an assigned branch (422 otherwise).</summary>
public static class PractitionerEndpoints
{
    public static void MapPractitioners(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:read"));
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:write"));

        // --- Reference specialties (org data) ----------------------------------------------------
        read.MapGet("/specialties", async (ProviderDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Specialties.AsNoTracking().Where(s => !s.IsDeleted).OrderBy(s => s.NameEn).ToListAsync(ct))
                .Select(s => new { s.SpecialtyCode, s.NameEn, s.NameAr, s.ParentCode })));

        // --- Create a practitioner ---------------------------------------------------------------
        write.MapPost("/practitioners", async (CreatePractitioner req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
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
            if (p is null) return Results.NotFound();
            if (!await db.Specialties.AnyAsync(s => s.SpecialtyCode == req.SpecialtyCode && !s.IsDeleted, ct))
                return Results.Problem(statusCode: 400, title: $"unknown specialty '{req.SpecialtyCode}'");

            db.PractitionerSpecialties.Add(new PractitionerSpecialty { PractitionerId = id, SpecialtyCode = req.SpecialtyCode, IsPrimary = req.IsPrimary });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "specialty already assigned or a primary specialty already exists"); }
            await audit.EmitAsync(Draft(p, AuditAction.Update, me, tenant, "specialty-assigned", req.SpecialtyCode), ct);
            return Results.Ok(new { p.PractitionerId, req.SpecialtyCode, req.IsPrimary });
        });

        // --- Assign a branch (a doctor may serve one-or-many) ------------------------------------
        write.MapPost("/practitioners/{id:guid}/branches", async (Guid id, AssignPractitionerBranch req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.NotFound();

            db.PractitionerBranchAssignments.Add(new PractitionerBranchAssignment
            {
                AssignmentId = Guid.NewGuid(), PractitionerId = id, BranchId = req.BranchId,
                ValidFrom = req.ValidFrom, ValidTo = req.ValidTo, Status = "Active",
            });
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(p, AuditAction.Update, me, tenant, "branch-assigned", req.BranchId.ToString()), ct);
            return Results.Ok(new { p.PractitionerId, req.BranchId });
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
        read.MapGet("/practitioners/{id:guid}/serves-branch", async (Guid id, Guid branchId, ProviderDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
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
