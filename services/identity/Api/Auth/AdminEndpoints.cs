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
            HttpContext http, Guid membershipId, IEffectiveSetService effective) =>
        {
            var (_, err) = await Guard(http, "admin:read");
            if (err is not null) return err;

            var set = await effective.ForMembershipAsync(membershipId, http.RequestAborted);
            return set is null
                ? Results.Problem(statusCode: 404, title: "not-found")
                : Results.Ok(new
                {
                    membershipId,
                    scopes = set.Keys.OrderBy(k => k, StringComparer.Ordinal),
                    deprecated = set.DeprecatedInUse.Select(d => new { key = d.Key, replacedBy = d.ReplacedBy }),
                });
        });
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
