using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
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

        // 25.2 (design 42 §2) — the write group admits the BRANCH authority alongside the network-wide one.
        // A clinic coordinator has to be able to assign a locum and maintain a licence at their own branch;
        // the only scope that previously did so was `provider:write`, which also creates branches and edits
        // external labs, pharmacies and tariffs. Sizing the scope to the clinic is the point.
        //
        // The scope group is HALF the control. `BranchReachGuard` is the other half: a caller holding only
        // the branch scope is enforced to the branches they actually run. Widening this line without that
        // check would be strictly worse than leaving these endpoints on `provider:write` — it would hand
        // every coordinator the whole network's roster while looking, in the route table, like a carefully
        // sized permission.
        var write = app.MapGroup("/api/v1")
            .RequireAuthorization(HbmpPolicies.AnyScope("provider:write", "branch:practitioner:write"));

        // --- Reference specialties (org data) ----------------------------------------------------
        read.MapGet("/specialties", async (ProviderDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Specialties.AsNoTracking().Where(s => !s.IsDeleted).OrderBy(s => s.NameEn).ToListAsync(ct))
                .Select(s => new SpecialtyView(s.SpecialtyCode, s.NameEn, s.NameAr, s.ParentCode))))
        .Produces<IEnumerable<SpecialtyView>>();

        // --- Create a practitioner ---------------------------------------------------------------
        // D3 (ADR-0029): a branch coordinator MAY create a practitioner. Central-only creation makes every new
        // locum a ticket to head office. The guard is licence uniqueness, below — which matters MORE now that
        // six clinics can each create in good faith without seeing one another's roster.
        write.MapPost("/practitioners", async (CreatePractitioner req, ProviderDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrEmpty(tenant)) return Results.Problem(statusCode: 403, title: "no tenant scope on principal");
            if (!Enum.TryParse<PractitionerType>(req.PractitionerType, out var type))
                return Results.Problem(statusCode: 400, title: $"unknown practitioner_type '{req.PractitionerType}'");

            // 25.2 (design 42 §2) — ONE practitioner identity, many branch assignments.
            //
            // Checked here so the answer can be USEFUL. `ux_practitioner_license_no` would refuse this write
            // anyway, and a bare 409 tells the coordinator "no" without telling them the one thing that
            // resolves it: this doctor already exists, and what they want is an assignment, not a record. The
            // id travels in the problem detail so the UI can offer "assign them to my clinic instead".
            //
            // The index remains the authority — this is a nicer message in front of it, not a replacement for
            // it. A concurrent create still lands on the DbUpdateException path below.
            if (!string.IsNullOrWhiteSpace(req.LicenseNo))
            {
                var existing = await db.Practitioners.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.LicenseNo == req.LicenseNo && !x.IsDeleted, ct);
                if (existing is not null) return LicenceConflict(existing);
            }

            var now = clock.GetUtcNow();
            var p = new Practitioner
            {
                PractitionerId = Guid.NewGuid(), TenantId = tenant, UserId = req.UserId, PractitionerType = type,
                FullNameEn = req.FullNameEn, FullNameAr = req.FullNameAr, LicenseNo = req.LicenseNo,
                LicenseExpiry = req.LicenseExpiry, Status = PractitionerStatus.Active, CreatedAt = now, UpdatedAt = now,
            };
            db.Practitioners.Add(p);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // Two live uniqueness rules now, and they mean different things to the person on the screen:
                // one user may hold one practitioner profile, and one licence belongs to one practitioner.
                // Re-reading tells us which, and the licence case gets the assign-instead path.
                db.ChangeTracker.Clear();
                if (!string.IsNullOrWhiteSpace(req.LicenseNo))
                {
                    var clash = await db.Practitioners.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.LicenseNo == req.LicenseNo && !x.IsDeleted, ct);
                    if (clash is not null) return LicenceConflict(clash);
                }
                return Results.Problem(statusCode: 409, title: "a practitioner already exists for this user");
            }
            await audit.EmitAsync(Draft(p, AuditAction.Create, me, tenant, "created"), ct);
            return Results.Created($"/api/v1/practitioners/{p.PractitionerId}", await ViewAsync(db, p.PractitionerId, canSeeLicense: true, ct));
        });

        // --- Maintain a practitioner's licence ---------------------------------------------------
        //
        // 25.2 — the field existed since 0006 and nothing could write it after creation, so a renewed licence
        // could not be recorded at all. That is not a cosmetic gap once 25.3 makes expiry a booking gate: the
        // renewal that keeps a doctor bookable had no way in.
        //
        // Reach-checked through the practitioner's own branches — a coordinator maintains the licence of a
        // doctor who works at their clinic, and has no business editing one who does not.
        write.MapPost("/practitioners/{id:guid}/licence", async (Guid id, UpdatePractitionerLicence req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, BranchReachGuard reach, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.Include(x => x.BranchAssignments)
                .FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            if (await reach.RefuseUnlessServesAReachableBranchAsync(id, ActiveBranches(p), ct) is { } denied) return denied;

            if (string.IsNullOrWhiteSpace(req.LicenseNo))
                return Results.Problem(statusCode: 400, title: "license_no is required");
            if (req.LicenseExpiry is null)
                return Results.Problem(statusCode: 400, title: "license_expiry is required",
                    detail: "An expiry date is what makes the licence enforceable (25.3). A licence with no " +
                            "expiry cannot be checked as at a slot date, so it is refused rather than stored.");

            // Same uniqueness rule as create, and the same reason: a renewal must not become the second row
            // holding a licence number.
            var clash = await db.Practitioners.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LicenseNo == req.LicenseNo && x.PractitionerId != id && !x.IsDeleted, ct);
            if (clash is not null) return LicenceConflict(clash);

            var before = $"{p.LicenseNo}|{p.LicenseExpiry:yyyy-MM-dd}";
            var previousExpiry = p.LicenseExpiry;
            p.LicenseNo = req.LicenseNo;
            p.LicenseExpiry = req.LicenseExpiry;
            p.UpdatedAt = clock.GetUtcNow();

            // 25.3 — a licence that moves EARLIER strands appointments beyond the new date, and the sweeper
            // will not catch it: the sweeper matches thresholds on exact days, so a correction back-dating an
            // expiry to last month crosses none of them and would announce nothing, ever.
            //
            // Emitted for any shortening, not only for a date already in the past. Bringing an expiry forward
            // from December to September invalidates October and November just as surely, and "it is still in
            // the future" is not a reason to leave those appointments unflagged.
            var shortened = req.LicenseExpiry is { } newExpiry
                && (previousExpiry is null || newExpiry < previousExpiry);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return LicenceConflict(clash ?? p); }

            if (shortened)
            {
                var branches = ActiveBranches(p);
                var payload = new
                {
                    tenantId = tenant,
                    practitionerId = p.PractitionerId,
                    fullNameEn = p.FullNameEn,
                    fullNameAr = p.FullNameAr,
                    licenceExpiry = p.LicenseExpiry,
                    branchIds = branches,
                };
                // Both copies, for the same reason the branch revocation needs two: the transport is
                // point-to-point, so emr cannot bind `provider.events` without competing for it.
                await outbox.EnqueueAsync("PractitionerLicenceExpired", "provider.events", payload, ct);
                await outbox.EnqueueAsync("PractitionerLicenceExpired", "emr.practitioner-licence-expired", payload, ct);
            }

            await audit.EmitAsync(Draft(p, AuditAction.Update, me, tenant, "licence-updated", before), ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { p.PractitionerId, p.LicenseNo, p.LicenseExpiry });
        });

        // --- Assign a specialty (one primary enforced by partial-unique index → 409) --------------
        write.MapPost("/practitioners/{id:guid}/specialties", async (Guid id, AssignSpecialty req, ProviderDbContext db, IAuditClient audit, BranchReachGuard reach, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.Include(x => x.BranchAssignments)
                .FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (await reach.RefuseUnlessServesAReachableBranchAsync(id, ActiveBranches(p), ct) is { } denied) return denied;
            // 25.2 — a coordinator ASSIGNS from the seeded 26. Creating or renaming a specialty is master
            // data for the whole network and has no endpoint here at all; this validation is what keeps the
            // catalogue closed, and SpecialtyCatalogueIsClosedTests fails the build if a write appears.
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
        write.MapPost("/practitioners/{id:guid}/specialties/revoke", async (Guid id, RevokeSpecialty req, ProviderDbContext db, IAuditClient audit, BranchReachGuard reach, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.Include(x => x.Specialties).Include(x => x.BranchAssignments)
                .FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (await reach.RefuseUnlessServesAReachableBranchAsync(id, ActiveBranches(p), ct) is { } denied) return denied;

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
        write.MapPost("/practitioners/{id:guid}/specialties/primary", async (Guid id, AssignSpecialty req, ProviderDbContext db, IAuditClient audit, BranchReachGuard reach, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.Include(x => x.Specialties).Include(x => x.BranchAssignments)
                .FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (await reach.RefuseUnlessServesAReachableBranchAsync(id, ActiveBranches(p), ct) is { } denied) return denied;
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
        write.MapPost("/practitioners/{id:guid}/branches", async (Guid id, AssignPractitionerBranch req, ProviderDbContext db, IAuditClient audit, BranchReachGuard reach, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            // 25.2 — the TARGET branch is what is checked, not the practitioner's existing ones. A coordinator
            // at Maadi assigning a practitioner to Dokki is 403 + audit; this is the canonical case in
            // design 42 §2, and it is an assignment INTO a clinic the caller does not run.
            if (await reach.RefuseUnlessInReachAsync(req.BranchId, "practitioner", id.ToString(), ct) is { } denied) return denied;

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
        write.MapPost("/practitioners/{id:guid}/branches/revoke", async (Guid id, RevokePractitionerBranch req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, BranchReachGuard reach, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (await reach.RefuseUnlessInReachAsync(req.BranchId, "practitioner", id.ToString(), ct) is { } denied) return denied;

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
                new { tenantId = tenant, practitionerId = id, branchId = req.BranchId, revoked = rows.Count }, ct);
            /*
             * THE COPY emr-service IS ACTUALLY LISTENING FOR (audit §11.3 item 5).
             *
             * The §11 sweep recorded this as "a consumer on a queue nothing publishes to". It is narrower and
             * worse than that: this line HAS published `PractitionerBranchRevoked` all along — to
             * `provider.events`, while `PractitionerBranchRevokedConsumer` binds `emr.practitioner-branch-revoked`.
             * A publish to a queue with no matching consumer does not fail, so both halves looked wired.
             *
             * The transport is point-to-point, so emr cannot simply bind `provider.events`: it would COMPETE
             * for those messages with whatever else consumes that stream. Hence the second copy, the same
             * decision the auth decisions and the registration enrolments already made.
             *
             * The `tenantId` was missing too, and that would have dead-lettered every message even on the
             * right queue — the consumer refuses to flag another organisation's appointments under a guessed
             * tenant. It is on both copies now; `provider.events` had subscribers that never needed it, which
             * is exactly how a field goes missing without anyone noticing.
             */
            await outbox.EnqueueAsync("PractitionerBranchRevoked", "emr.practitioner-branch-revoked",
                new { tenantId = tenant, practitionerId = id, branchId = req.BranchId, revoked = rows.Count }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { p.PractitionerId, req.BranchId, Revoked = rows.Count });
        });

        // --- Change a practitioner's status ------------------------------------------------------
        //
        // The picker feed below returns Active practitioners only, so suspending one removes them from every
        // booking screen without deleting a record that appointments and encounters still reference.
        write.MapPost("/practitioners/{id:guid}/status", async (Guid id, ChangePractitionerStatus req, ProviderDbContext db, IAuditClient audit, BranchReachGuard reach, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.Problem(statusCode: 400, title: "a reason is required");
            if (!Enum.TryParse<PractitionerStatus>(req.Status, out var status))
                return Results.Problem(statusCode: 400, title: $"unknown status '{req.Status}'");

            var tenant = me.Principal?.TenantId;
            var p = await db.Practitioners.Include(x => x.BranchAssignments)
                .FirstOrDefaultAsync(x => x.PractitionerId == id && x.TenantId == tenant && !x.IsDeleted, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (await reach.RefuseUnlessServesAReachableBranchAsync(id, ActiveBranches(p), ct) is { } denied) return denied;

            p.Status = status;
            p.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(p, AuditAction.StateChange, me, tenant, $"status-{status}", req.Reason), ct);
            return Results.Ok(new { p.PractitionerId, Status = p.Status.ToString() });
        });

        // --- Doctor picker: filter by branch + specialty + type; min-necessary projection --------
        read.MapGet("/practitioners", async (Guid? branchId, string? specialtyCode, string? type, DateOnly? asOf, bool? includeUnlicensed, ProviderDbContext db, IHbmpPrincipalAccessor me, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var canSeeLicense = CanSeeLicence(me.Principal);
            var on = asOf ?? calendar.Today();
            var q = db.Practitioners.AsNoTracking().Include(x => x.Specialties).Include(x => x.BranchAssignments)
                .Where(x => x.TenantId == tenant && !x.IsDeleted && x.Status == PractitionerStatus.Active);
            if (Enum.TryParse<PractitionerType>(type, out var t)) q = q.Where(x => x.PractitionerType == t);
            if (branchId is { } b) q = q.Where(x => x.BranchAssignments.Any(a => a.BranchId == b && a.Status == "Active"));
            if (!string.IsNullOrWhiteSpace(specialtyCode)) q = q.Where(x => x.Specialties.Any(s => s.SpecialtyCode == specialtyCode));
            var rows = await q.OrderBy(x => x.FullNameEn).Take(200).ToListAsync(ct);

            // 25.3 — the PICKER excludes practitioners who hold no valid licence AS AT the date being booked.
            // This is the gate at its earliest and kindest point: an operator never sees, and so never offers,
            // a doctor who cannot lawfully take that appointment. It is NOT the enforcement — emr's slot and
            // booking gates are, because a UI filter is a courtesy and any client that skips this call would
            // otherwise book freely.
            //
            // `includeUnlicensed=true` is for the coordinator's own admin screen, which must show exactly the
            // people a picker hides — a licence worklist that cannot list expired licences is no worklist.
            if (includeUnlicensed != true)
                rows = [.. rows.Where(p => PractitionerLicence.IsValidAt(p.LicenseExpiry, on))];

            return Results.Ok(rows.Select(p => ToView(p, canSeeLicense, on)));
        })
        .Produces<IEnumerable<PractitionerView>>();

        // --- Licence alerts worklist (the coordinator's screen) ----------------------------------
        //
        // 25.3 (design 42 §6) — expiring and expired licences at the branches the caller runs. This is the
        // worklist the sweeper's warnings point AT: an alert that tells someone a licence lapses in 30 days
        // and gives them nowhere to go is a notification, not a control.
        //
        // Reach-scoped, not tenant-scoped: a coordinator sees their own clinic's practitioners, a clinics
        // manager sees all six in ONE response (BranchSetScoped, 25.1) — same endpoint, no separate
        // "manager" route.
        read.MapGet("/practitioners/licence-alerts", async (int? withinDays, BranchScopeState branch, BranchReachGuard reach, ProviderDbContext db, IHbmpPrincipalAccessor me, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var today = calendar.Today();
            var horizon = today.AddDays(Math.Clamp(withinDays ?? 90, 1, 365));

            var rows = await db.Practitioners.AsNoTracking()
                .Include(x => x.BranchAssignments)
                .Where(x => x.TenantId == tenant && !x.IsDeleted
                            && x.Status == PractitionerStatus.Active
                            && x.LicenseExpiry != null && x.LicenseExpiry <= horizon)
                .OrderBy(x => x.LicenseExpiry)
                .Take(500)
                .ToListAsync(ct);

            // Narrowed to the caller's reach in memory rather than in SQL because the reach rule lives in ONE
            // place (BranchReachGuard → AbacConditions) and re-expressing it as a predicate here is how the
            // coordinator's rule and the manager's rule drift apart. The row cap above bounds the cost.
            var visible = rows
                .Where(p => reach.IsNetworkWide || ActiveBranches(p).Any(reach.CanReach))
                .Select(p => new
                {
                    p.PractitionerId,
                    p.FullNameEn,
                    p.FullNameAr,
                    PractitionerType = p.PractitionerType.ToString(),
                    // The licence NUMBER obeys the same field-mask as everywhere else; the DATE does not,
                    // because the date is what the four-cue status chip renders (design 42 §6).
                    LicenseNo = CanSeeLicence(me.Principal) ? p.LicenseNo : null,
                    p.LicenseExpiry,
                    DaysUntilExpiry = PractitionerLicence.DaysUntilExpiry(p.LicenseExpiry, today),
                    // Three states, named, so the UI never has to derive "expired" from a negative number —
                    // deriving a safety status client-side is how a grey chip ends up meaning "may not
                    // legally practise".
                    Status = PractitionerLicence.IsValidAt(p.LicenseExpiry, today) ? "Expiring" : "Expired",
                    Branches = ActiveBranches(p).Where(b => reach.IsNetworkWide || reach.CanReach(b)).ToList(),
                })
                .ToList();

            return Results.Ok(new { asOf = today, withinDays = (horizon.DayNumber - today.DayNumber), alerts = visible });
        });

        // --- serves-branch probe: emr calls this to enforce booking/availability (422 if not) -----
        //
        // 25.3 — now answers AS AT A DATE, and answers about the LICENCE as well as the assignment.
        //
        // `asOf` exists because emr asks this question about a slot in October, not about today. Booking three
        // months ahead against a licence expiring next month has to fail at GENERATION — surprising a patient
        // on the day is the failure this phase exists to prevent — and that is unanswerable from a probe that
        // only knows about today. It defaults to today, so every pre-25.3 caller keeps its exact behaviour.
        //
        // The assignment window is evaluated at the same date for the same reason: an assignment that ends in
        // September does not make a doctor bookable in October.
        read.MapGet("/practitioners/{id:guid}/serves-branch", async (Guid id, Guid branchId, DateOnly? asOf, ProviderDbContext db, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var on = asOf ?? calendar.Today();   // 18.A3
            var covering = await db.PractitionerBranchAssignments.AsNoTracking()
                .Where(a => a.PractitionerId == id && a.BranchId == branchId && a.Status == "Active"
                            && a.ValidFrom <= on && (a.ValidTo == null || a.ValidTo >= on))
                .Select(a => a.ValidTo)
                .ToListAsync(ct);
            var serves = covering.Count > 0;

            // 25.4 — the LAST DAY of the assignment, so emr can bound slot generation by it as well as by the
            // licence. Without this, generating three months of slots for a locum whose contract ends next
            // week produces a calendar that looks entirely healthy until the patient arrives.
            //
            // An open-ended assignment (valid_to NULL) bounds nothing, and a practitioner with several
            // overlapping assignments to one branch is bounded by the LATEST — they keep working there while
            // any of them runs.
            DateOnly? assignmentValidTo = covering.Any(v => v is null) ? null : covering.Max();

            var p = await db.Practitioners.AsNoTracking()
                .Where(x => x.PractitionerId == id && !x.IsDeleted)
                .Select(x => new { x.LicenseNo, x.LicenseExpiry, x.Status })
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                practitionerId = id,
                branchId,
                asOf = on,
                servesBranch = serves,
                // Absent practitioner ⇒ licence UNKNOWN rather than valid. emr's null-object seam decides what
                // unknown means for its own operation; answering "valid" here would make the gate vacuous for
                // exactly the ids that do not resolve.
                licenceValid = p is null ? (bool?)null : PractitionerLicence.IsValidAt(p.LicenseExpiry, on),
                licenceExpiry = p?.LicenseExpiry,
                // The NUMBER is never returned by this probe — emr has no business holding staff licence
                // numbers, and the field-mask on the picker exists for the same reason.
                licenceEnforceable = p is not null && PractitionerLicence.IsEnforceable(p.LicenseNo, p.LicenseExpiry),
                assignmentValidTo,
            });
        });
    }

    /// <summary>
    /// 25.2 — the licence field-mask, extended to the branch authority (design 42 §3).
    ///
    /// The mask itself is unchanged in spirit: a licence number is staff PII and is absent from the payload
    /// for anyone who does not maintain licences. What changed is WHO maintains them. Before this, the only
    /// way to see a licence number was `provider:write` — the network-wide scope — so a coordinator could
    /// not do the job the design gives them without being handed the external provider directory as well.
    ///
    /// Deliberately NOT widened to `practitioner:read`. Reception and the call centre hold that scope for the
    /// booking pickers, and a licence number is not something the front desk needs to book an appointment.
    /// </summary>
    private static bool CanSeeLicence(HbmpPrincipal? p) =>
        (p?.HasScope("provider:write") ?? false) || (p?.HasScope("branch:practitioner:write") ?? false);

    /// <summary>
    /// 409 for a licence that already belongs to someone (design 42 §2). The existing id travels in the
    /// problem detail deliberately: the coordinator's next action is "assign them to my clinic instead", and
    /// a 409 that withholds the id leaves them re-typing the licence number into a search box to find the
    /// record the server just looked at. The NAME goes too, because "practitioner 8f3a-…" is not something a
    /// person can confirm is the right doctor.
    ///
    /// This is a deliberate, narrow disclosure to a caller who already holds practitioner-administration
    /// authority — it says nothing about branches, patients or clinical data.
    /// </summary>
    private static IResult LicenceConflict(Practitioner existing) => Results.Problem(
        statusCode: 409, title: "practitioner-exists", type: "urn:hbmp:practitioner-exists",
        detail: $"Licence '{existing.LicenseNo}' already belongs to {existing.FullNameEn}. " +
                "Assign them to your branch instead of creating a second record.",
        extensions: new Dictionary<string, object?>
        {
            ["practitionerId"] = existing.PractitionerId,
            ["fullNameEn"] = existing.FullNameEn,
            ["fullNameAr"] = existing.FullNameAr,
            ["licenseNo"] = existing.LicenseNo,
        });

    /// <summary>The branches a practitioner ACTIVELY serves — the set a branch-scoped caller's reach is tested
    /// against for edits that do not name a branch of their own (licence, specialty, status).</summary>
    private static IReadOnlyCollection<Guid> ActiveBranches(Practitioner p) =>
        [.. p.BranchAssignments.Where(a => a.Status == "Active").Select(a => a.BranchId)];

    private static PractitionerView ToView(Practitioner p, bool canSeeLicense, DateOnly? asOf = null) => new(
        p.PractitionerId, p.PractitionerType.ToString(), p.FullNameEn, p.FullNameAr,
        p.Specialties.FirstOrDefault(s => s.IsPrimary)?.SpecialtyCode,
        p.Specialties.Select(s => s.SpecialtyCode).ToList(),
        p.BranchAssignments.Where(a => a.Status == "Active").Select(a => a.BranchId).ToList(),
        p.Status.ToString(), canSeeLicense ? p.LicenseNo : null,
        // 25.3 — the EXPIRY travels even to callers who may not see the NUMBER. They are different
        // disclosures: the number identifies a person to a regulator, the date is what makes a chip on a
        // roster say "expires in 12 days". Withholding the date from the coordinator's screen would leave the
        // four-cue licence status with nothing to render.
        p.LicenseExpiry,
        asOf is { } on ? PractitionerLicence.IsValidAt(p.LicenseExpiry, on) : null,
        asOf is { } d ? PractitionerLicence.DaysUntilExpiry(p.LicenseExpiry, d) : null);

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
