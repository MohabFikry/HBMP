using System.Security.Claims;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// In-app user / role / scope administration (17.4) — the management half of audit finding C3. Every action
/// requires a bearer token with the <c>admin:*</c> scope AND an MFA session (amr), and writes a hash-chained
/// audit event via the shared client. No hard deletes — deprovision is a soft <c>is_active=false</c>. Roles
/// and the role→scope matrix are data, so this edits them in place (the 17.5 admin SPA calls these).
/// </summary>
public static class AdminEndpoints
{
    private const string Bearer = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;

    public static void MapAdmin(this WebApplication app)
    {
        // 18.B3 (S3) — the framework enforces authn + an admin scope + MFA before routing reaches a handler.
        // Guard remains as layer two for the per-action read/write distinction and its problem bodies.
        var g = app.MapGroup("/identity/admin").RequireAuthorization(IdentityAdminPolicies.Admin);

        // ---- Users -----------------------------------------------------------------------------------------
        g.MapGet("/users", async (HttpContext http, string? query, UserManager<ApplicationUser> users, IdentityStoreDbContext db) =>
        {
            var (_, err) = await Guard(http, "admin:read");
            if (err is not null) return err;

            var q = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(query))
            {
                var norm = query.ToUpperInvariant();
                q = q.Where(u => u.NormalizedUserName!.Contains(norm) || EF.Functions.ILike(u.DisplayName, $"%{query}%"));
            }
            var rows = await q.OrderBy(u => u.UserName).Take(200).ToListAsync(http.RequestAborted);
            var views = new List<object>();
            foreach (var u in rows)
                views.Add(new
                {
                    id = u.Id, username = u.UserName, displayName = u.DisplayName,
                    tenantId = u.TenantId, providerId = u.ProviderId, isActive = u.IsActive,
                    twoFactorEnabled = u.TwoFactorEnabled, roles = await users.GetRolesAsync(u),
                });
            return Results.Ok(views);
        });

        g.MapPost("/users", async (HttpContext http, CreateUserRequest req,
            UserManager<ApplicationUser> users, IAuditClient audit, TimeProvider clock, MembershipService memberships) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var known = req.Roles.All(r => IdentityContract.Roles.Contains(r.ToLowerInvariant()));
            if (!known) return Results.Problem(statusCode: 422, title: "unknown-role", detail: "one or more roles are not in the catalog");

            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(), UserName = req.Username, NormalizedUserName = req.Username.ToUpperInvariant(),
                Email = req.Email, DisplayName = req.DisplayName, TenantId = req.TenantId, ProviderId = req.ProviderId,
                CreatedAt = clock.GetUtcNow(), IsActive = true,
            };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded)
                return Results.Problem(statusCode: 422, title: "create-failed", detail: string.Join("; ", created.Errors.Select(e => e.Description)));
            if (req.Roles.Count > 0) await users.AddToRolesAsync(user, req.Roles.Select(r => r.ToLowerInvariant()));

            // 21.1c — give the new account the membership that IS its principal. Without this it could sign in
            // and then be refused at authorize, because 0010's backfill only covered users that already existed.
            await memberships.EnsureMirroredAsync(user, req.Roles.Select(r => r.ToLowerInvariant()),
                me!.GetClaim(Claims.Subject) ?? "admin", http.RequestAborted);

            await Audit(audit, me, "identity.user", user.Id.ToString(), AuditAction.Create, "UserCreated",
                $"{{\"username\":\"{user.UserName}\",\"roles\":[{string.Join(",", req.Roles.Select(r => $"\"{r}\""))}]}}");
            return Results.Created($"/identity/admin/users/{user.Id}", new { id = user.Id, username = user.UserName });
        });

        g.MapPost("/users/{id:guid}/roles", async (HttpContext http, Guid id, SetRolesRequest req,
            UserManager<ApplicationUser> users, IAuditClient audit, MembershipService memberships) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.Problem(statusCode: 404, title: "not-found");
            var desired = req.Roles.Select(r => r.ToLowerInvariant()).Distinct().ToHashSet();
            if (!desired.All(IdentityContract.Roles.Contains))
                return Results.Problem(statusCode: 422, title: "unknown-role");

            var current = (await users.GetRolesAsync(user)).ToHashSet();
            await users.AddToRolesAsync(user, desired.Except(current));
            await users.RemoveFromRolesAsync(user, current.Except(desired));

            // 21.1c — the membership is what the token is minted from, so a role REMOVED here has to be removed
            // there too. Mirroring only additions would make revocation cosmetic.
            await memberships.EnsureMirroredAsync(user, desired, me!.GetClaim(Claims.Subject) ?? "admin", http.RequestAborted);

            await Audit(audit, me, "identity.user", id.ToString(), AuditAction.Update, "UserRolesSet",
                $"{{\"roles\":[{string.Join(",", desired.Select(r => $"\"{r}\""))}]}}");
            return Results.Ok(new { id, roles = desired });
        });

        g.MapPost("/users/{id:guid}/deactivate", async (HttpContext http, Guid id,
            UserManager<ApplicationUser> users, IAuditClient audit, MembershipService memberships,
            SessionService sessions) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.Problem(statusCode: 404, title: "not-found");
            user.IsActive = false;                       // soft deprovision — never a hard delete (audit trail)
            await users.UpdateSecurityStampAsync(user);  // invalidate existing sessions/refresh
            await users.UpdateAsync(user);

            // 21.1c — deprovision has to reach the principal, not just the identity. An Active membership left
            // behind would still resolve and still mint tokens, so the account would remain usable.
            await memberships.EnsureMirroredAsync(user, await users.GetRolesAsync(user),
                me!.GetClaim(Claims.Subject) ?? "admin", http.RequestAborted);

            // 21.5 — and it has to reach the live SESSIONS. UpdateSecurityStampAsync above does not revoke
            // OpenIddict refresh tokens: the token endpoint checks IsActive but never compares security
            // stamps, so before this line an off-boarded account kept every session it already had until
            // each one happened to refresh. Fails CLOSED (A6) — an administrator who is told the account is
            // deprovisioned must not be told that on the strength of a revocation that did not persist.
            await sessions.RevokeAllAsync(id, me!.GetClaim(Claims.Subject) ?? "admin",
                "account deactivated", http.RequestAborted);

            await Audit(audit, me, "identity.user", id.ToString(), AuditAction.Update, "UserDeactivated", null);
            return Results.Ok(new { id, isActive = false });
        });

        g.MapPost("/users/{id:guid}/reset-password", async (HttpContext http, Guid id, ResetPasswordRequest req,
            UserManager<ApplicationUser> users, IAuditClient audit) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.Problem(statusCode: 404, title: "not-found");
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var reset = await users.ResetPasswordAsync(user, token, req.NewPassword);
            if (!reset.Succeeded)
                return Results.Problem(statusCode: 422, title: "reset-failed", detail: string.Join("; ", reset.Errors.Select(e => e.Description)));
            await users.UpdateSecurityStampAsync(user);

            await Audit(audit, me, "identity.user", id.ToString(), AuditAction.Update, "UserPasswordReset", null);
            return Results.Ok(new { id });
        });

        // ---- Role → scope matrix (data) --------------------------------------------------------------------
        g.MapPost("/roles/{role}/scopes", async (HttpContext http, string role, SetRoleScopesRequest req,
            IdentityStoreDbContext db, IAuditClient audit) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            role = role.ToLowerInvariant();
            if (!IdentityContract.Roles.Contains(role)) return Results.Problem(statusCode: 404, title: "unknown-role");
            var catalog = (await db.Scopes.Select(s => s.Name).ToListAsync(http.RequestAborted)).ToHashSet(StringComparer.Ordinal);
            if (!req.Scopes.All(catalog.Contains)) return Results.Problem(statusCode: 422, title: "unknown-scope");

            // 21.1b — grants are TENANT-LOCAL (design 40 §2). Edit only the caller's own tenant: before this,
            // one administrator's edit silently rewrote the grant set every tenant resolved through.
            // Provisioning another tenant's grants is a platform-administration action and gets its own
            // surface in 21.6; it is deliberately not reachable by passing a tenant here.
            var tenant = me!.GetClaim(HbmpClaimTypes.TenantId) ?? RoleScope.PlatformDefault;

            var existing = await db.RoleScopes
                .Where(rs => rs.RoleName == role && rs.TenantId == tenant)
                .ToListAsync(http.RequestAborted);
            db.RoleScopes.RemoveRange(existing);
            foreach (var s in req.Scopes.Distinct(StringComparer.Ordinal))
                db.RoleScopes.Add(new RoleScope { TenantId = tenant, RoleName = role, ScopeName = s });
            await db.SaveChangesAsync(http.RequestAborted);

            await Audit(audit, me, "identity.role_scope", $"{tenant}/{role}", AuditAction.Update, "RoleScopesSet",
                $"{{\"tenant\":\"{tenant}\",\"scopes\":[{string.Join(",", req.Scopes.Select(s => $"\"{s}\""))}]}}");
            return Results.Ok(new { tenant, role, scopes = req.Scopes });
        });

        // ---- Membership administration reads (21.6, design 40 §1 + §6) -------------------------------------
        //
        // The roster behind the admin UI. Deliberately NOT the access-review snapshot: that recomputes every
        // membership's effective set and is audited as an EXPORT, because signing a review pack is a bulk
        // disclosure of the tenant's whole posture. Browsing a list is not. Reusing it here would make the
        // screen O(memberships) evaluator calls to render a table that shows none of those keys, and would
        // bury the real exports under routine navigation — the audit trail's value is that Export means
        // something.
        //
        // TENANT-PINNED. The tenant is the isolation boundary (§1), so a caller sees its OWN tenant unless the
        // identity carries the platform-admin flag — and per A1 that flag buys administrative reach only,
        // which is exactly what a membership roster is. It is not a step towards PHI.
        g.MapGet("/memberships", async (
            HttpContext http, string? tenant, string? status, string? query,
            IdentityStoreDbContext db, IAuditClient audit, TimeProvider clock) =>
        {
            var (me, err) = await Guard(http, "admin:read");
            if (err is not null) return err;

            var (scopeTenant, denied) = await ResolveTenantReachAsync(db, me!, tenant, audit, http.RequestAborted);
            if (denied is not null) return denied;

            MembershipStatus? wanted = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse(status, ignoreCase: true, out MembershipStatus parsed))
                    return Results.Problem(statusCode: 422, title: "unknown-status",
                        detail: $"status must be one of {string.Join(", ", Enum.GetNames<MembershipStatus>())}");
                wanted = parsed;
            }

            var q = db.Memberships.AsNoTracking().Where(m => !m.IsDeleted);
            if (scopeTenant is not null) q = q.Where(m => m.TenantId == scopeTenant);
            if (wanted is not null) q = q.Where(m => m.Status == wanted);

            if (!string.IsNullOrWhiteSpace(query))
            {
                var norm = query.ToUpperInvariant();
                var matching = db.Users.AsNoTracking()
                    .Where(u => u.NormalizedUserName!.Contains(norm) || EF.Functions.ILike(u.DisplayName, $"%{query}%"))
                    .Select(u => u.Id);
                q = q.Where(m => matching.Contains(m.UserId));
            }

            var rows = await q
                .OrderBy(m => m.TenantId).ThenBy(m => m.CreatedAt)
                .Take(500)
                .ToListAsync(http.RequestAborted);

            // Batched, not per-row: the access review can afford a query per membership because it runs once
            // for a signed report; a screen someone pages through cannot.
            var views = await ProjectAsync(db, rows, clock.GetUtcNow(), http.RequestAborted);

            await Audit(audit, me, "identity.tenant_membership", scopeTenant ?? "(all-tenants)",
                AuditAction.Read, "MembershipRosterRead",
                $"{{\"tenant\":\"{scopeTenant ?? "*"}\",\"count\":{views.Count}}}");
            return Results.Ok(views);
        });

        // One membership in full — the detail screen's roles/overrides tabs. Branch grants and programme
        // enablement are NOT copied in here: they live in admin-service and the UI composes the two calls,
        // for the same reason the access review references them rather than reading across the boundary.
        g.MapGet("/memberships/{membershipId:guid}", async (
            HttpContext http, Guid membershipId,
            IdentityStoreDbContext db, IAuditClient audit, TimeProvider clock) =>
        {
            var (me, err) = await Guard(http, "admin:read");
            if (err is not null) return err;

            var membership = await db.Memberships.AsNoTracking()
                .FirstOrDefaultAsync(m => m.MembershipId == membershipId && !m.IsDeleted, http.RequestAborted);
            if (membership is null) return Results.Problem(statusCode: 404, title: "not-found");

            // The tenant check happens AFTER the lookup but the answer is the same either way — a caller
            // outside the tenant gets 403 whether or not the row exists. Returning 404 for out-of-tenant rows
            // would leak nothing here (the id is a v4 guid), but 403 is the honest answer to "may I".
            var (_, denied) = await ResolveTenantReachAsync(db, me!, membership.TenantId, audit, http.RequestAborted);
            if (denied is not null) return denied;

            var now = clock.GetUtcNow();
            var view = (await ProjectAsync(db, [membership], now, http.RequestAborted))[0];

            var overrides = await db.Overrides.AsNoTracking()
                .Where(o => o.MembershipId == membershipId && !o.IsDeleted)
                .OrderBy(o => o.ScopeKey)
                .ToListAsync(http.RequestAborted);

            await Audit(audit, me, "identity.tenant_membership", membershipId.ToString(),
                AuditAction.Read, "MembershipRead", $"{{\"tenant\":\"{membership.TenantId}\"}}");

            return Results.Ok(new
            {
                view.MembershipId, view.UserId, view.Username, view.DisplayName, view.TenantId,
                view.Status, view.ProviderId, view.HomeBranchId, view.Roles, view.Level,
                view.IsPlatformAdmin, view.ActivatedAt, view.EndedAt,
                // The counts are part of the roster row's shape, and the detail is a SUPERSET of that row —
                // the SPA's MembershipDetail contract extends MembershipRow, so omitting them here makes the
                // detail fail its own contract while the list passes.
                view.OverrideCount, view.ExpiredOverrideCount,
                // Every override with its reason and grantor: an exception shown without them cannot be
                // judged, and a reviewer who cannot judge one either rubber-stamps it or escalates all of
                // them (the failure mode 21.5's review pack was shaped to avoid).
                overrides = overrides.Select(o => new
                {
                    id = o.OverrideId, scope = o.ScopeKey, effect = o.Effect.ToString(),
                    reason = o.Reason, grantedBy = o.GrantedBy, validUntil = o.ValidUntil,
                    // Lapsed overrides are LISTED as expired rather than filtered out. The evaluator already
                    // ignores them; hiding them here would leave an administrator unable to see why someone's
                    // access changed last night.
                    expired = o.ValidUntil is not null && o.ValidUntil <= now,
                }),
            });
        });

        // ---- Per-membership overrides (21.2, design 40 §2) -------------------------------------------------
        //
        // The exception path. It is the most dangerous surface in this service — a way to hand one person a
        // key their role does not carry — so it is the most constrained: a reason is mandatory, the grant is
        // vetted by the SAME SoD engine that vets role grants, and every outcome is audited including the
        // refusals.
        g.MapPost("/memberships/{membershipId:guid}/overrides", async (
            HttpContext http, Guid membershipId, SetOverrideRequest req,
            IdentityStoreDbContext db, MembershipService memberships, IEffectiveSetService effective,
            IAuditClient audit, TimeProvider clock) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.Problem(statusCode: 422, title: "reason-required",
                    detail: "an override without a reason cannot be reviewed later");
            if (req.Effect is not (nameof(OverrideEffect.Allow) or nameof(OverrideEffect.Deny)))
                return Results.Problem(statusCode: 422, title: "unknown-effect", detail: "effect must be Allow or Deny");

            var membership = await db.Memberships
                .FirstOrDefaultAsync(m => m.MembershipId == membershipId && !m.IsDeleted, http.RequestAborted);
            if (membership is null) return Results.Problem(statusCode: 404, title: "not-found");

            if (!await db.Scopes.AnyAsync(s => s.Name == req.ScopeKey, http.RequestAborted))
                return Results.Problem(statusCode: 422, title: "unknown-scope");

            var effect = Enum.Parse<OverrideEffect>(req.Effect);

            // SoD — an Allow override goes through the same conflict matrix as a role grant. An exception
            // path that skipped this would simply be the supported way to hold both halves of a duty the
            // matrix splits, which is worse than no exception path at all. A DENY is always safe: taking
            // authority away cannot create a forbidden combination.
            if (effect == OverrideEffect.Allow)
            {
                var held = await memberships.RolesForAsync(membershipId, http.RequestAborted);
                var violations = SegregationOfDuties.EvaluateScopeGrant(held, req.ScopeKey);
                if (violations.Count > 0)
                {
                    await Audit(audit, me, "identity.membership_override", membershipId.ToString(),
                        AuditAction.Update, "OverrideRefusedSoD",
                        $"{{\"scope\":\"{req.ScopeKey}\",\"conflicts\":[{string.Join(",", violations.Select(v => $"\"{v.ConflictingToken}\""))}]}}");
                    return Results.Problem(statusCode: 409, title: "sod-conflict",
                        detail: string.Join("; ", violations.Select(v => $"{v.HeldToken} vs {v.ConflictingToken}: {v.Reason}")));
                }
            }

            var now = clock.GetUtcNow();
            var actor = me!.GetClaim(Claims.Subject) ?? "admin";
            var existing = await db.Overrides.FirstOrDefaultAsync(
                o => o.MembershipId == membershipId && o.ScopeKey == req.ScopeKey && !o.IsDeleted, http.RequestAborted);

            if (existing is null)
            {
                existing = new MembershipOverride
                {
                    OverrideId = Guid.NewGuid(), MembershipId = membershipId, ScopeKey = req.ScopeKey,
                    Effect = effect, Reason = req.Reason, GrantedBy = actor, ValidUntil = req.ValidUntil,
                    CreatedBy = actor, CreatedAt = now, UpdatedBy = actor, UpdatedAt = now,
                };
                db.Overrides.Add(existing);
            }
            else
            {
                existing.Effect = effect;
                existing.Reason = req.Reason;
                existing.ValidUntil = req.ValidUntil;
                existing.GrantedBy = actor;
                existing.UpdatedBy = actor;
                existing.UpdatedAt = now;
                existing.RowVersion++;
            }

            db.OverrideHistory.Add(HistoryOf(existing, actor, now, "override set (21.2)"));
            await db.SaveChangesAsync(http.RequestAborted);

            // RE-RESOLUTION (design 40 §5): mode 2 must never serve authority that has just been changed.
            // The in-session token keeps its old set until it expires — that is the documented staleness
            // window, bounded by the short access TTL, and the next refresh recomputes from the store.
            effective.Invalidate(membershipId);

            await Audit(audit, me, "identity.membership_override", existing.OverrideId.ToString(),
                AuditAction.Update, "OverrideSet",
                $"{{\"membership\":\"{membershipId}\",\"scope\":\"{req.ScopeKey}\",\"effect\":\"{effect}\"}}");
            return Results.Ok(new
            {
                id = existing.OverrideId, membershipId, scope = req.ScopeKey,
                effect = effect.ToString(), validUntil = existing.ValidUntil,
            });
        });

        g.MapDelete("/memberships/{membershipId:guid}/overrides/{scopeKey}", async (
            HttpContext http, Guid membershipId, string scopeKey,
            IdentityStoreDbContext db, IEffectiveSetService effective, IAuditClient audit, TimeProvider clock) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var existing = await db.Overrides.FirstOrDefaultAsync(
                o => o.MembershipId == membershipId && o.ScopeKey == scopeKey && !o.IsDeleted, http.RequestAborted);
            if (existing is null) return Results.Problem(statusCode: 404, title: "not-found");

            var now = clock.GetUtcNow();
            var actor = me!.GetClaim(Claims.Subject) ?? "admin";
            // Soft delete — an override is evidence of a decision, and revoking it must not erase the record
            // that it once existed (CLAUDE.md § Audit — no hard deletes).
            existing.IsDeleted = true;
            existing.UpdatedBy = actor;
            existing.UpdatedAt = now;
            existing.RowVersion++;
            db.OverrideHistory.Add(HistoryOf(existing, actor, now, "override revoked (21.2)"));
            await db.SaveChangesAsync(http.RequestAborted);

            effective.Invalidate(membershipId);

            await Audit(audit, me, "identity.membership_override", existing.OverrideId.ToString(),
                AuditAction.SoftDelete, "OverrideRevoked", $"{{\"membership\":\"{membershipId}\",\"scope\":\"{scopeKey}\"}}");
            return Results.Ok(new { id = existing.OverrideId, membershipId, scope = scopeKey, revoked = true });
        });

        // "What would this person actually see" — mode 2, which is the only way to answer it without
        // minting a token for someone else.
        g.MapGet("/memberships/{membershipId:guid}/effective", async (
            HttpContext http, Guid membershipId, IEffectiveSetService effective, IdentityStoreDbContext db) =>
        {
            var (_, err) = await Guard(http, "admin:read");
            if (err is not null) return err;

            var set = await effective.ForMembershipAsync(membershipId, http.RequestAborted);
            if (set is null) return Results.Problem(statusCode: 404, title: "not-found");

            // 21.6 — which of these keys are platform-ADMINISTRATION keys.
            //
            // The evaluator adds them under the A1 short-circuit but the EffectiveSet records only the union,
            // so a preview reading the union alone would label a key the flag granted as "from role" — the
            // one provenance mistake that matters here, because A1 is the invariant people most want to see
            // the boundary of. Returned as the catalog's own marking rather than a recomputation: this
            // reports metadata, it does not re-run the algebra (the parity suite covers that, and a third
            // implementation would be a third opinion).
            var adminKeys = await db.Scopes.AsNoTracking()
                .Where(s => s.IsPlatformAdminKey)
                .Select(s => s.Name)
                .ToListAsync(http.RequestAborted);

            return Results.Ok(new
            {
                membershipId,
                scopes = set.Keys.OrderBy(k => k, StringComparer.Ordinal),
                deprecated = set.DeprecatedInUse.Select(d => new { key = d.Key, replacedBy = d.ReplacedBy }),
                platformAdminKeys = adminKeys.Where(set.Keys.Contains).OrderBy(k => k, StringComparer.Ordinal),
            });
        });
    }

    // ---- 21.6 membership projection ----------------------------------------------------------------------

    /// <summary>
    /// Resolve which tenant this caller may read, and refuse — loudly — if it is not the one asked for.
    ///
    /// Per A2, a denied privileged read is audited rather than quietly narrowed to the caller's own tenant.
    /// Silently rewriting the filter would show an administrator a page of THEIR tenant under another
    /// tenant's heading, which is worse than an error: they would review the wrong organisation and believe
    /// they had reviewed the right one.
    /// </summary>
    /// <returns>The tenant to filter on (null = every tenant, platform admin only), or the 403 to return.</returns>
    private static async Task<(string? Tenant, IResult? Denied)> ResolveTenantReachAsync(
        IdentityStoreDbContext db, ClaimsPrincipal me, string? requested, IAuditClient audit, CancellationToken ct)
    {
        var own = me.GetClaim(HbmpClaimTypes.TenantId);
        var isPlatformAdmin = Guid.TryParse(me.GetClaim(Claims.Subject), out var sub)
            && await db.Users.AsNoTracking().Where(u => u.Id == sub).Select(u => u.IsPlatformAdmin)
                .FirstOrDefaultAsync(ct);

        // No tenant asked for: a platform admin legitimately means "all of them", anyone else means "mine".
        if (string.IsNullOrWhiteSpace(requested))
            return isPlatformAdmin ? (null, null) : (own, null);

        if (isPlatformAdmin || string.Equals(requested, own, StringComparison.Ordinal))
            return (requested, null);

        await Audit(audit, me, "identity.tenant_membership", requested, AuditAction.Read,
            "CrossTenantMembershipReadDenied", $"{{\"requested\":\"{requested}\",\"own\":\"{own}\"}}");
        return (null, Results.Problem(statusCode: 403, title: "cross-tenant-read-denied",
            detail: "reading another tenant's memberships requires the platform-administration flag"));
    }

    /// <summary>Project memberships for the roster, batching the user/role/override lookups so the cost does
    /// not scale with the page size.</summary>
    private static async Task<IReadOnlyList<MembershipView>> ProjectAsync(
        IdentityStoreDbContext db, IReadOnlyList<TenantMembership> rows, DateTimeOffset now, CancellationToken ct)
    {
        var ids = rows.Select(m => m.MembershipId).ToList();
        var userIds = rows.Select(m => m.UserId).Distinct().ToList();

        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName, u.DisplayName, u.IsPlatformAdmin })
            .ToDictionaryAsync(u => u.Id, ct);

        var roles = (await db.MembershipRoles.AsNoTracking()
                .Where(mr => ids.Contains(mr.MembershipId))
                .Join(db.Roles, mr => mr.RoleId, r => r.Id,
                    (mr, r) => new { mr.MembershipId, r.Name, r.Level })
                .ToListAsync(ct))
            .GroupBy(x => x.MembershipId)
            .ToDictionary(gr => gr.Key, gr => gr.ToList());

        var overrides = (await db.Overrides.AsNoTracking()
                .Where(o => ids.Contains(o.MembershipId) && !o.IsDeleted)
                .Select(o => new { o.MembershipId, o.ValidUntil })
                .ToListAsync(ct))
            .GroupBy(x => x.MembershipId)
            .ToDictionary(gr => gr.Key, gr => gr.ToList());

        return [.. rows.Select(m =>
        {
            var held = roles.TryGetValue(m.MembershipId, out var r) ? r : [];
            var ovr = overrides.TryGetValue(m.MembershipId, out var o) ? o : [];
            users.TryGetValue(m.UserId, out var u);

            return new MembershipView(
                m.MembershipId, m.UserId, u?.UserName ?? "(unknown)", u?.DisplayName ?? "",
                m.TenantId, m.Status.ToString(), m.ProviderId, m.HomeBranchId,
                [.. held.Select(x => new MembershipRoleView(x.Name ?? "", x.Level))
                        .OrderBy(x => x.Name, StringComparer.Ordinal)],
                // "Lower = more privileged", so the most privileged tier held is the MINIMUM — the same
                // convention the access review uses, and the only one that answers "is this an
                // administrative persona" correctly for someone holding two roles.
                held.Where(x => x.Level.HasValue).Select(x => x.Level!.Value).DefaultIfEmpty().Min(),
                u?.IsPlatformAdmin ?? false, m.ActivatedAt, m.EndedAt,
                ovr.Count,
                // Surfaced as its own count so the list can badge it: an override quietly lapsing is a
                // change in someone's authority that nobody requested and nobody is told about.
                ovr.Count(x => x.ValidUntil is not null && x.ValidUntil <= now));
        })];
    }

    private static MembershipOverrideHistory HistoryOf(
        MembershipOverride o, string actor, DateTimeOffset now, string reason) => new()
    {
        OverrideId = o.OverrideId, MembershipId = o.MembershipId, ScopeKey = o.ScopeKey,
        Effect = o.Effect.ToString(), Reason = o.Reason, ValidUntil = o.ValidUntil,
        IsDeleted = o.IsDeleted, RowVersion = o.RowVersion, ChangedBy = actor, ChangedAt = now,
        ChangeReason = reason,
    };

    // ---- guard + audit ------------------------------------------------------------------------------------

    /// <summary>Authenticate the bearer (OpenIddict validation), require the <paramref name="scope"/> AND an
    /// MFA session. Returns the caller principal, or an RFC-7807 error to short-circuit with.</summary>
    private static async Task<(ClaimsPrincipal? Me, IResult? Error)> Guard(HttpContext http, string scope)
    {
        var auth = await http.AuthenticateAsync(Bearer);
        if (!auth.Succeeded || auth.Principal is null)
            return (null, Results.Problem(statusCode: 401, title: "unauthenticated"));

        var p = auth.Principal;
        if (!p.HasScope(scope))
            return (null, Results.Problem(statusCode: 403, title: "insufficient-scope", detail: $"requires {scope}"));

        var amr = p.GetClaims(AccountPages.AmrClaim);
        if (!MfaEvaluator.IsSatisfied(p.GetClaim(HbmpClaimTypes.Acr), amr))
            return (null, Results.Problem(statusCode: 403, title: "mfa-required", detail: "admin actions require a step-up (MFA) session"));

        return (p, null);
    }

    private static async Task Audit(IAuditClient audit, ClaimsPrincipal? me, string entityType, string entityId,
        AuditAction action, string outcome, string? afterState) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = entityType, EntityId = entityId, Action = action,
            ActorUserId = me?.GetClaim(Claims.Subject), DecisionOutcome = outcome, AfterState = afterState,
        });

    // ---- views -------------------------------------------------------------------------------------------

    /// <summary>One role as the roster shows it. <c>Level</c> is nullable because a role without a tier is a
    /// real state (nothing has classified it yet) and defaulting it to 0 would read as "most privileged".</summary>
    public sealed record MembershipRoleView(string Name, int? Level);

    /// <summary>A membership as the 21.6 admin roster shows it — authority (roles, level, overrides) but no
    /// reach: branch grants live in admin-service and the UI composes them (design 40 §3).</summary>
    public sealed record MembershipView(
        Guid MembershipId, Guid UserId, string Username, string DisplayName, string TenantId,
        string Status, Guid? ProviderId, Guid? HomeBranchId,
        IReadOnlyList<MembershipRoleView> Roles, int Level, bool IsPlatformAdmin,
        DateTimeOffset? ActivatedAt, DateTimeOffset? EndedAt,
        int OverrideCount, int ExpiredOverrideCount);

    // ---- requests ----------------------------------------------------------------------------------------

    /// <summary>21.2 — set or replace one per-membership override. <c>Reason</c> is mandatory by contract as
    /// well as by schema: an unexplained exception is indistinguishable from a mistake at review time.</summary>
    public sealed record SetOverrideRequest(
        string ScopeKey, string Effect, string Reason, DateTimeOffset? ValidUntil = null);

    public sealed record CreateUserRequest(
        string Username, string DisplayName, string Password, string TenantId,
        string? Email = null, Guid? ProviderId = null, IReadOnlyList<string> Roles = null!)
    {
        public IReadOnlyList<string> Roles { get; init; } = Roles ?? [];
    }
    public sealed record SetRolesRequest(IReadOnlyList<string> Roles);
    public sealed record ResetPasswordRequest(string NewPassword);
    public sealed record SetRoleScopesRequest(IReadOnlyList<string> Scopes);
}
