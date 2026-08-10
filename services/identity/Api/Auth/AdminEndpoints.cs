using System.Security.Claims;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Email;
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
                // EMAIL is searched too, and it is the field an administrator is most likely to be handed:
                // a request to reset an account arrives as an address far more often than as a username.
                q = q.Where(u => u.NormalizedUserName!.Contains(norm)
                              || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(norm))
                              || EF.Functions.ILike(u.DisplayName, $"%{query}%"));
            }
            var rows = await q.OrderBy(u => u.UserName).Take(200).ToListAsync(http.RequestAborted);
            var views = new List<object>();
            foreach (var u in rows)
                views.Add(new
                {
                    id = u.Id, username = u.UserName, displayName = u.DisplayName,
                    // Returned since 28.8: the console could not show an address, so an administrator could
                    // not tell whether "send a reset link" would reach anybody before pressing it.
                    email = u.Email,
                    tenantId = u.TenantId, providerId = u.ProviderId, isActive = u.IsActive,
                    twoFactorEnabled = u.TwoFactorEnabled, roles = await users.GetRolesAsync(u),
                });
            return Results.Ok(views);
        });

        g.MapPost("/users", async (HttpContext http, CreateUserRequest req,
            UserManager<ApplicationUser> users, IAuditClient audit, TimeProvider clock, MembershipService memberships,
            IEmailSender email, IConfiguration config, ILoggerFactory logs) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var known = req.Roles.All(r => IdentityContract.Roles.Contains(r.ToLowerInvariant()));
            if (!known) return Results.Problem(statusCode: 422, title: "unknown-role", detail: "one or more roles are not in the catalog");

            // 28.8 — an email address is REQUIRED now, because it is the sign-in credential and the only
            // channel a password reset can travel down. An account created without one can neither sign in
            // by address nor be helped back in, and nothing about it says so until somebody is locked out.
            if (string.IsNullOrWhiteSpace(req.Email) || !IsPlausibleEmail(req.Email))
                return Results.Problem(statusCode: 422, title: "email-required",
                    detail: "a valid email address is required — it is the sign-in credential and the reset channel");

            // Checked BEFORE the create so the conflict is reported as a conflict. Identity's own uniqueness
            // check would surface as a generic "create-failed" with a validation string in the detail, which
            // an administrator cannot distinguish from a rejected password. The database's unique index
            // (0035) is what actually enforces this; the check here exists to give a good answer.
            if (await users.FindByEmailAsync(req.Email) is not null)
                return Results.Problem(statusCode: 409, title: "email-taken",
                    detail: "another account already uses this email address");
            if (await users.FindByNameAsync(req.Username) is not null)
                return Results.Problem(statusCode: 409, title: "username-taken",
                    detail: "another account already uses this username");

            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(), UserName = req.Username, NormalizedUserName = req.Username.ToUpperInvariant(),
                Email = req.Email, DisplayName = req.DisplayName, TenantId = req.TenantId, ProviderId = req.ProviderId,
                CreatedAt = clock.GetUtcNow(), IsActive = true,
            };
            // ------------------------------------------------------------------------------------------------
            // 28.8 — THE ADMINISTRATOR DOES NOT CHOOSE THE PASSWORD, HERE EITHER.
            // ------------------------------------------------------------------------------------------------
            // 28.7 removed the admin's ability to SET a password on an existing account, on the grounds that
            // there must be a moment at which only its owner knows the credential. Creation was left taking
            // a `password`, which answered the same question the other way: the person who filled the form
            // in knew the new account's password, and had to communicate it down some channel that outlives
            // the moment.
            //
            // The password is now a throwaway. It is generated here, never returned, never logged, and never
            // valid for anybody because the reset link below is what the owner actually uses. `req.Password`
            // remains accepted so the seeders and integration tests that mint fixture accounts keep working;
            // no UI sends it.
            var initial = req.Password is { Length: > 0 } chosen ? chosen : GenerateThrowawayPassword();
            var created = await users.CreateAsync(user, initial);
            if (!created.Succeeded)
                return Results.Problem(statusCode: 422, title: "create-failed", detail: string.Join("; ", created.Errors.Select(e => e.Description)));
            if (req.Roles.Count > 0) await users.AddToRolesAsync(user, req.Roles.Select(r => r.ToLowerInvariant()));

            // 21.1c — give the new account the membership that IS its principal. Without this it could sign in
            // and then be refused at authorize, because 0010's backfill only covered users that already existed.
            await memberships.EnsureMirroredAsync(user, req.Roles.Select(r => r.ToLowerInvariant()),
                me!.GetClaim(Claims.Subject) ?? "admin", http.RequestAborted);

            // The invitation. It is what turns a row in a table into an account somebody can use, so its
            // outcome is REPORTED rather than assumed: a failure here leaves a real account nobody can sign
            // in to, and an administrator told only "created" would walk away from that.
            //
            // It does NOT fail the creation. The account and its roles are already committed and correct;
            // undoing them because a mail server was down would throw away the work and leave the
            // administrator with nothing, when the remedy is one button (Send reset link) on the row that
            // now exists.
            var invited = false;
            if (email.IsConfigured && !string.IsNullOrWhiteSpace(user.Email))
            {
                try
                {
                    await PasswordResetEndpoints.SendResetLinkAsync(users, email, config, user, req.Lang, http.RequestAborted);
                    invited = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logs.CreateLogger("Mersal.Identity.Api.Auth.AdminEndpoints")
                        .LogError(ex, "Invitation link could not be sent for new user {UserId}.", user.Id);
                }
            }

            await Audit(audit, me, "identity.user", user.Id.ToString(), AuditAction.Create, "UserCreated",
                $"{{\"username\":\"{user.UserName}\",\"invited\":{invited.ToString().ToLowerInvariant()},\"roles\":[{string.Join(",", req.Roles.Select(r => $"\"{r}\""))}]}}");
            return Results.Created($"/identity/admin/users/{user.Id}",
                new { id = user.Id, username = user.UserName, email = user.Email, resetLinkSent = invited });
        });

        // ---- 28.8 — correct an account's name or address --------------------------------------------------
        //
        // Without this, fixing a typo in an email meant creating a SECOND account: the address is the sign-in
        // credential, so a wrong one is not a cosmetic defect, and the only remedy available was to deprovision
        // and start again — losing the audit continuity of the person, which is exactly what soft-deprovision
        // exists to preserve.
        //
        // Roles are NOT settable here. They go through `/roles`, which mirrors the membership the token is
        // minted from; letting authority change through a "fix the spelling" endpoint would put a grant on a
        // path nobody reviews.
        g.MapPost("/users/{id:guid}", async (HttpContext http, Guid id, UpdateUserRequest req,
            UserManager<ApplicationUser> users, IAuditClient audit) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.Problem(statusCode: 404, title: "not-found");

            if (!string.IsNullOrWhiteSpace(req.Email) &&
                !string.Equals(req.Email, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsPlausibleEmail(req.Email))
                    return Results.Problem(statusCode: 422, title: "invalid-email", detail: "that is not a usable email address");
                var clash = await users.FindByEmailAsync(req.Email);
                if (clash is not null && clash.Id != user.Id)
                    return Results.Problem(statusCode: 409, title: "email-taken",
                        detail: "another account already uses this email address");
                await users.SetEmailAsync(user, req.Email);
            }
            if (!string.IsNullOrWhiteSpace(req.DisplayName)) user.DisplayName = req.DisplayName;

            var saved = await users.UpdateAsync(user);
            if (!saved.Succeeded)
                return Results.Problem(statusCode: 422, title: "update-failed",
                    detail: string.Join("; ", saved.Errors.Select(e => e.Description)));

            await Audit(audit, me, "identity.user", id.ToString(), AuditAction.Update, "UserUpdated",
                $"{{\"displayName\":\"{user.DisplayName}\",\"email\":\"{user.Email}\"}}");
            return Results.Ok(new { id, displayName = user.DisplayName, email = user.Email });
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

        // ---- 28.8 — and the way back ----------------------------------------------------------------------
        //
        // Deprovision shipped without it. A staff member returning from leave, or an account disabled by
        // mistake, could only be restored by an UPDATE run against the database by hand — which is both
        // unaudited and exactly the kind of access this whole service exists to remove the need for.
        //
        // It restores the account and its membership; it does NOT restore the sessions deactivation revoked,
        // and it must not. Those were ended deliberately and the person signs in again — which is also what
        // produces a fresh, correct token rather than resurrecting one minted before whatever caused the
        // deprovision.
        g.MapPost("/users/{id:guid}/reactivate", async (HttpContext http, Guid id,
            UserManager<ApplicationUser> users, IAuditClient audit, MembershipService memberships) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.Problem(statusCode: 404, title: "not-found");
            user.IsActive = true;
            await users.UpdateAsync(user);

            // Symmetric with deactivate: the membership is what the token is minted from, so an account
            // restored here without its membership restored there would sign in and then be refused at
            // authorize — a "working" account that works right up until the moment it matters.
            await memberships.EnsureMirroredAsync(user, await users.GetRolesAsync(user),
                me!.GetClaim(Claims.Subject) ?? "admin", http.RequestAborted);

            await Audit(audit, me, "identity.user", id.ToString(), AuditAction.Update, "UserReactivated", null);
            return Results.Ok(new { id, isActive = true });
        });

        // ---- 28.7 — an administrator ISSUES A LINK. They no longer choose the password. ----------------------
        //
        // ============================================================================================================
        // WHY THE OLD SHAPE HAD TO GO
        // ============================================================================================================
        // This took `{ "newPassword": "..." }`, so the administrator CHOSE and therefore KNEW the credential, and
        // there was no moment at which only its owner did. Shipping self-service reset (28.6) while leaving that
        // in place would answer "is a password a secret only its owner knows?" both ways at once.
        //
        // It also produced a password that had to be communicated somehow — by phone, by chat, on paper — and
        // every one of those channels outlives the moment. A link that expires in 30 minutes and dies on first
        // use does not.
        //
        // What an administrator keeps is the ABILITY TO START a reset for somebody who cannot start their own.
        // What they lose is knowledge of the result. That is the whole change.
        g.MapPost("/users/{id:guid}/reset-password", async (
            HttpContext http, Guid id, AdminResetRequest? req,
            UserManager<ApplicationUser> users, IEmailSender email, IConfiguration config,
            IAuditClient audit, ILoggerFactory logs) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.Problem(statusCode: 404, title: "not-found");

            // Unlike the self-service endpoint, this one may be BLUNT. The caller is an authenticated
            // administrator who already holds the user id, so "this account has no email address" tells them
            // nothing they could not already learn and is exactly what they need in order to do something
            // else about it. Vagueness here would be security theatre with a real cost.
            if (!email.IsConfigured)
                return Results.Problem(statusCode: 503, title: "email-not-configured",
                    detail: "No email transport is configured, so a reset link cannot be delivered.");
            if (string.IsNullOrWhiteSpace(user.Email))
                return Results.Problem(statusCode: 422, title: "no-email-address",
                    detail: "This account has no email address, so a reset link cannot be sent to it.");

            try
            {
                await PasswordResetEndpoints.SendResetLinkAsync(
                    users, email, config, user, req?.Lang, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Told, not swallowed. An administrator who believes they have started a reset and has not is
                // worse off than one who knows it failed — they will wait for a call that never comes.
                logs.CreateLogger("Mersal.Identity.Api.Auth.AdminEndpoints")
                    .LogError(ex, "Administrative password-reset link could not be sent for {UserId}.", id);
                return Results.Problem(statusCode: 502, title: "send-failed",
                    detail: "The reset link could not be sent. Nothing has changed on the account.");
            }

            // The ADMINISTRATOR is the actor and the user is the subject — the opposite of the self-service
            // event, and the distinction is the point of recording it: a reset somebody else started is a
            // different fact from one you started yourself.
            await Audit(audit, me, "identity.user", id.ToString(), AuditAction.Update, "UserPasswordResetLinkSent", null);
            return Results.Ok(new { id, resetLinkSent = true });
        });

        // ---- 28.9 — THE ACCESS CATALOGUE -------------------------------------------------------------------
        //
        // ============================================================================================================
        // WHY THIS HAD TO EXIST BEFORE CUSTOM ROLES COULD
        // ============================================================================================================
        // Every permission in the platform has always been data — `identity.scope` — and no surface listed it.
        // An administrator could grant a role, and could grant an exception naming a key, but had no way to
        // find out what keys there ARE or what any of them means. In practice that leaves exactly one usable
        // strategy: give somebody the nearest bigger role and hope. Which is how least-privilege dies — not
        // by being rejected, but by being unavailable.
        //
        // Everything here is a READ of the catalog. Nothing is authorized by it and nothing is disclosed by
        // it beyond the vocabulary the token contract already publishes.
        g.MapGet("/scopes", async (HttpContext http, IdentityStoreDbContext db) =>
        {
            var (me, err) = await Guard(http, "admin:read");
            if (err is not null) return err;

            var tenant = me!.GetClaim(HbmpClaimTypes.TenantId) ?? RoleScope.PlatformDefault;

            // Which roles hold each key, IN THIS TENANT. The question an administrator actually has in front
            // of a permission is "who has this already" — without it, deciding whether a new role needs a key
            // means guessing, and the safe guess is always "include it".
            var grants = await db.RoleScopes.AsNoTracking()
                .Where(rs => rs.TenantId == tenant || rs.TenantId == RoleScope.PlatformDefault)
                .ToListAsync(http.RequestAborted);
            // A tenant that has provisioned its own grants OVERRIDES the platform default rather than adding
            // to it — the same precedence the resolver applies, restated here so the screen does not show a
            // role holding a key the issuer would not actually mint for it.
            var tenantOwn = grants.Where(rs => rs.TenantId == tenant).Select(rs => rs.RoleName).ToHashSet(StringComparer.Ordinal);
            var effective = grants
                .Where(rs => tenantOwn.Contains(rs.RoleName) ? rs.TenantId == tenant : true)
                .GroupBy(rs => rs.ScopeName)
                .ToDictionary(gr => gr.Key, gr => gr.Select(x => x.RoleName).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());

            var scopes = await db.Scopes.AsNoTracking()
                .OrderBy(s => s.Domain).ThenBy(s => s.Name)
                .ToListAsync(http.RequestAborted);

            return Results.Ok(scopes.Select(s => new
            {
                name = s.Name,
                domain = s.Domain,
                description = s.Description,
                // Every flag the catalog carries, because each one changes whether a key belongs in a role:
                // a service-only key must never reach a human, a deprecated one must not seed a new role, and
                // a platform-administration key is the one kind the A1 short-circuit can reach.
                serviceOnly = s.ServiceOnly,
                deprecated = s.Deprecated,
                replacedBy = s.ReplacedBy,
                isPlatformAdminKey = s.IsPlatformAdminKey,
                heldBy = effective.TryGetValue(s.Name, out var roles) ? roles : [],
            }));
        });

        /// The role catalogue: what each role is, and what it actually grants in THIS tenant.
        g.MapGet("/roles", async (HttpContext http, IdentityStoreDbContext db) =>
        {
            var (me, err) = await Guard(http, "admin:read");
            if (err is not null) return err;

            var tenant = me!.GetClaim(HbmpClaimTypes.TenantId) ?? RoleScope.PlatformDefault;
            var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(http.RequestAborted);
            var grants = await db.RoleScopes.AsNoTracking()
                .Where(rs => rs.TenantId == tenant || rs.TenantId == RoleScope.PlatformDefault)
                .ToListAsync(http.RequestAborted);

            var own = grants.Where(rs => rs.TenantId == tenant).Select(rs => rs.RoleName).ToHashSet(StringComparer.Ordinal);
            var byRole = grants
                .Where(rs => own.Contains(rs.RoleName) ? rs.TenantId == tenant : true)
                .GroupBy(rs => rs.RoleName)
                .ToDictionary(gr => gr.Key, gr => gr.Select(x => x.ScopeName).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());

            return Results.Ok(roles
                // Another tenant's custom role is not shown: it is not assignable here and not editable here,
                // so listing it would only offer a control that refuses.
                .Where(r => r.OwnerTenantId is null || r.OwnerTenantId == tenant)
                .Select(r => new
                {
                    name = r.Name,
                    description = r.Description,
                    sensitivityTier = r.SensitivityTier,
                    level = r.Level,
                    // A built-in role's scope set is platform policy and is edited with care; a custom one is
                    // this tenant's own and is edited freely. The UI needs to say which is which.
                    custom = r.OwnerTenantId is not null,
                    builtIn = IdentityContract.Roles.Contains(r.Name ?? ""),
                    scopes = byRole.TryGetValue(r.Name ?? "", out var s) ? s : [],
                }));
        });

        /// <summary>28.9 — design a role: a name, a tier, and a set of permissions chosen from the catalogue.</summary>
        g.MapPost("/roles", async (HttpContext http, CreateRoleRequest req,
            RoleManager<ApplicationRole> roles, IdentityStoreDbContext db, IAuditClient audit) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            var tenant = me!.GetClaim(HbmpClaimTypes.TenantId) ?? RoleScope.PlatformDefault;
            var name = (req.Name ?? "").Trim().ToLowerInvariant();

            // The name lands in the token's `roles` claim, which every service parses. Constrained to the
            // same shape the built-ins use so a custom role cannot smuggle whitespace, a colon or a comma
            // into a claim that other code splits on.
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9_]{2,48}$"))
                return Results.Problem(statusCode: 422, title: "invalid-role-name",
                    detail: "a role name is 3–49 characters of lower-case letters, digits and underscores");

            // A built-in name is refused outright rather than merged into: the platform's role definitions are
            // not a tenant's to redefine, and silently editing one here would change what `doctor` means for
            // everybody who reads the audit trail expecting the standard meaning.
            if (IdentityContract.Roles.Contains(name))
                return Results.Problem(statusCode: 409, title: "reserved-role-name",
                    detail: "that is a built-in role; edit its permissions instead of redefining it");
            if (await roles.RoleExistsAsync(name))
                return Results.Problem(statusCode: 409, title: "role-name-taken",
                    detail: "a role with this name already exists");

            var catalog = (await db.Scopes.AsNoTracking().ToListAsync(http.RequestAborted))
                .ToDictionary(s => s.Name, StringComparer.Ordinal);
            var wanted = (req.Scopes ?? []).Distinct(StringComparer.Ordinal).ToList();
            var unknown = wanted.Where(s => !catalog.ContainsKey(s)).ToList();
            if (unknown.Count > 0)
                return Results.Problem(statusCode: 422, title: "unknown-scope",
                    detail: $"not in the catalogue: {string.Join(", ", unknown)}");

            // A machine key on a human role is a category error the catalogue already records, so it is
            // refused rather than merely discouraged: `auth:ingest` on somebody's account is a service
            // credential attached to a person, and no review would ever catch it as one.
            var machine = wanted.Where(s => catalog[s].ServiceOnly).ToList();
            if (machine.Count > 0)
                return Results.Problem(statusCode: 422, title: "service-only-scope",
                    detail: $"these keys are granted to machines, never to people: {string.Join(", ", machine)}");

            // SoD over the SET, not key by key — see SegregationOfDuties.EvaluateScopeSet. A role holding both
            // halves of a split duty breaches it for every person ever assigned the role, at once.
            var violations = SegregationOfDuties.EvaluateScopeSet(wanted);
            if (violations.Count > 0)
            {
                await Audit(audit, me, "identity.role", name, AuditAction.Create, "RoleRefusedSoD",
                    $"{{\"conflicts\":[{string.Join(",", violations.Select(v => $"\"{v.HeldToken} vs {v.ConflictingToken}\""))}]}}");
                return Results.Problem(statusCode: 409, title: "sod-conflict",
                    detail: string.Join("; ", violations.Select(v => $"{v.HeldToken} vs {v.ConflictingToken}: {v.Reason}")));
            }

            var tier = req.SensitivityTier is "T1" or "T2" or "T3" or "T4" ? req.SensitivityTier : "T2";
            var role = new ApplicationRole(name)
            {
                Id = Guid.NewGuid(),
                Description = req.Description,
                SensitivityTier = tier,
                // Seeded as 4 − tier, matching the built-ins: lower is more privileged, so a T4 persona
                // lands at 0. Derived rather than accepted from the caller, so tier and level cannot disagree.
                Level = 4 - int.Parse(tier[1..]),
                OwnerTenantId = tenant,
            };
            var created = await roles.CreateAsync(role);
            if (!created.Succeeded)
                return Results.Problem(statusCode: 422, title: "create-failed",
                    detail: string.Join("; ", created.Errors.Select(e => e.Description)));

            // Grants are TENANT-LOCAL (21.1b): the role is authored here, so its scope set belongs to this
            // tenant's bucket and not to the platform default every other tenant falls back to.
            foreach (var s in wanted)
                db.RoleScopes.Add(new RoleScope { TenantId = tenant, RoleName = name, ScopeName = s });
            await db.SaveChangesAsync(http.RequestAborted);

            await Audit(audit, me, "identity.role", name, AuditAction.Create, "RoleCreated",
                $"{{\"tenant\":\"{tenant}\",\"tier\":\"{tier}\",\"scopes\":[{string.Join(",", wanted.Select(s => $"\"{s}\""))}]}}");
            return Results.Created($"/identity/admin/roles/{name}",
                new { name, tier, level = role.Level, scopes = wanted, custom = true });
        });

        // ---- Role → scope matrix (data) --------------------------------------------------------------------
        g.MapPost("/roles/{role}/scopes", async (HttpContext http, string role, SetRoleScopesRequest req,
            IdentityStoreDbContext db, IAuditClient audit) =>
        {
            var (me, err) = await Guard(http, "admin:write");
            if (err is not null) return err;

            role = role.ToLowerInvariant();
            // 28.9 — a CUSTOM role is editable too, and it has to be: a role designed here that could never
            // be adjusted afterwards would have to be got right first time or abandoned. `IdentityContract.
            // Roles` alone answered 404 for every role this tenant had just authored.
            var custom = await db.Roles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Name == role && r.OwnerTenantId != null, http.RequestAborted);
            if (!IdentityContract.Roles.Contains(role) && custom is null)
                return Results.Problem(statusCode: 404, title: "unknown-role");
            // Another tenant's custom role is theirs. This is the same boundary the membership roster draws,
            // and it is the reason OwnerTenantId is recorded at all.
            if (custom is not null && custom.OwnerTenantId != (me!.GetClaim(HbmpClaimTypes.TenantId) ?? RoleScope.PlatformDefault))
                return Results.Problem(statusCode: 403, title: "not-your-role",
                    detail: "this role belongs to another organisation");
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
        if (IdentityAdminPolicies.MfaRequired && !MfaEvaluator.IsSatisfied(p.GetClaim(HbmpClaimTypes.Acr), amr))
            return (null, Results.Problem(statusCode: 403, title: "mfa-required", detail: "admin actions require a step-up (MFA) session"));

        return (p, null);
    }

    /// <summary>
    /// Is this a usable email address?
    ///
    /// <para>Deliberately shallow — one '@', something either side of it, a dot in the domain, no spaces.
    /// RFC 5322 permits addresses no mail server in this deployment would accept, and a regex claiming to
    /// implement it rejects real addresses (plus-tags, long TLDs, non-ASCII domains) while still admitting
    /// undeliverable ones. The only proof an address works is a message arriving at it, which is what the
    /// reset link is; this check exists to catch a typo before that, not to be an authority.</para>
    /// </summary>
    private static bool IsPlausibleEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) return false;
        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1) return false;
        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    /// <summary>
    /// A password for a new account that NOBODY knows, including the administrator creating it.
    /// </summary>
    /// <remarks>
    /// It exists only because ASP.NET Identity needs a hash to store; the account is reached through the
    /// reset link, never through this. Cryptographically random rather than a fixed placeholder, because a
    /// deployment where every freshly created account shares a known password until its owner clicks a link
    /// is a deployment with a standing back door. Never returned, never logged, never recoverable — if the
    /// link fails, the remedy is another link.
    /// </remarks>
    private static string GenerateThrowawayPassword()
    {
        // The generated string must satisfy the configured policy (12+, upper, lower, digit, symbol) or
        // CreateAsync refuses it — so the four required classes are appended explicitly rather than hoped for
        // out of 32 random bytes.
        var random = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        return $"Aa1!{random}";
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

    /// <summary>
    /// 28.8 — <c>Email</c> is required and <c>Password</c> is not.
    ///
    /// <para>The two moved in opposite directions in the same change, and for the same reason. The address is
    /// what the account signs in with and the only channel a reset can travel down, so an account without one
    /// is unreachable. The password is generated server-side and thrown away, so that there is no moment at
    /// which the administrator knows the credential (28.7's rule, applied to creation as well).</para>
    ///
    /// <para><c>Password</c> stays on the record, optional, for the seeders and integration tests that mint
    /// fixture accounts and then sign in as them. No UI sends it.</para>
    /// </summary>
    public sealed record CreateUserRequest(
        string Username, string DisplayName, string TenantId, string Email,
        string? Password = null, Guid? ProviderId = null, string? Lang = null,
        IReadOnlyList<string> Roles = null!)
    {
        public IReadOnlyList<string> Roles { get; init; } = Roles ?? [];
    }
    /// <summary>Correct an account's display name or address. Both optional; an omitted field is left alone
    /// rather than cleared, so a caller fixing one cannot silently erase the other.</summary>
    public sealed record UpdateUserRequest(string? DisplayName = null, string? Email = null);
    public sealed record SetRolesRequest(IReadOnlyList<string> Roles);
    /// <summary>
    /// 28.7 — an administrative reset carries a LANGUAGE, not a password.
    ///
    /// <para>`ResetPasswordRequest(string NewPassword)` is gone rather than deprecated. A record left in place
    /// is a shape somebody re-wires later; the endpoint that took it now issues a link, and there is nothing
    /// left for a new password to mean.</para>
    /// </summary>
    public sealed record AdminResetRequest(string? Lang);
    public sealed record SetRoleScopesRequest(IReadOnlyList<string> Scopes);

    /// <summary>
    /// 28.9 — a role designed by a tenant from the access catalogue.
    ///
    /// <para><c>Level</c> is absent on purpose: it is derived from <c>SensitivityTier</c> so the two cannot
    /// be set to disagree, which for an ordinal where lower means more privileged would be an invisible
    /// mistake.</para>
    /// </summary>
    public sealed record CreateRoleRequest(
        string Name, IReadOnlyList<string> Scopes, string? Description = null, string SensitivityTier = "T2");
}
