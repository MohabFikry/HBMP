using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Events;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Mersal.Time;

namespace Mersal.Provider.Api;

/// <summary>The Network Team onboarding workflow (2b.2, FR-NET-003/004/007): guarded activation,
/// provider-user provisioning with SoD, dual-controlled termination, de-provisioning on suspend/terminate,
/// and credential-expiry reminders. Every step writes a hash-chained audit_event with actor + justification.</summary>
public static class OnboardingEndpoints
{
    public static void MapOnboarding(this WebApplication app)
    {
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("provider:write"));

        // --- Activate: guarded by contract + valid mandatory credentials + primary location (FR-NET-004) ---
        write.MapPost("/providers/{id:guid}/activate", async (Guid id, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var (p, tenant) = await Load(db, id, me, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var readiness = ReadinessOf(p, Today(calendar));
            var guard = OnboardingWorkflow.GuardActivation(readiness);
            if (!guard.Allowed)
            {
                await audit.EmitAsync(Draft(p, AuditAction.StateChange, me, tenant, outcome: "activation-blocked", reason: guard.Reason), ct);
                return Results.Problem(statusCode: 422, title: "cannot activate provider", detail: guard.Reason);
            }

            // 24.3 — activation and the event that makes the provider ROUTABLE downstream commit together.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            p.Status = ProviderStatus.Active;
            p.OnboardingState = OnboardingState.Activated;
            p.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(p, AuditAction.StateChange, me, tenant, outcome: "Activated"), ct);
            await outbox.EnqueueAsync("ProviderStatusChanged", "provider.events", new { providerId = p.ProviderId, status = "Active", onboardingState = "Activated", tenantId = tenant }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { p.ProviderId, status = p.Status.ToString(), onboardingState = p.OnboardingState.ToString(), routable = true });
        });

        // --- Suspend: stop routing + revoke all provider users (FR-IAM-010) --------------------------------
        write.MapPost("/providers/{id:guid}/suspend", async (Guid id, StateChange req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.Problem(statusCode: 400, title: "a reason is required");
            var (p, tenant) = await Load(db, id, me, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            // 24.3 — suspension revokes the provider's users (an ExecuteUpdate, which commits on its own)
            // AND announces both facts. Split across three commits, a crash can leave users revoked while
            // the provider still reads Active downstream — or the reverse.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            p.Status = ProviderStatus.Suspended;
            p.OnboardingState = OnboardingState.Suspended;
            p.UpdatedAt = clock.GetUtcNow();
            var revoked = await RevokeUsers(db, id, clock, ct);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(p, AuditAction.StateChange, me, tenant, outcome: "Suspended", reason: req.Reason), ct);
            await outbox.EnqueueAsync("ProviderStatusChanged", "provider.events", new { providerId = p.ProviderId, status = "Suspended", tenantId = tenant }, ct);
            await outbox.EnqueueAsync("ProviderUsersRevoked", "provider.events", new { providerId = p.ProviderId, count = revoked, reason = "provider-suspended", tenantId = tenant }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { p.ProviderId, status = p.Status.ToString(), usersRevoked = revoked });
        });

        // --- Terminate: dual-controlled, two tokens -------------------------------------------------------
        //
        // This used to be one request that compared `req.SecondApproverSubject` against the actor's subject
        // and terminated if they differed. The "second approver" was a STRING the person terminating typed:
        // they never authenticated, never consented, and were never checked to exist. Naming a colleague was
        // the entire control, on an action that drops a provider out of the routable network, revokes every
        // provider-scoped user's access and publishes both facts platform-wide.
        //
        // Now the approver is whoever holds the bearer token on the SECOND call — the shape admin break-glass
        // already uses (Requested → Approved). First authorised POST opens a request and changes nothing
        // about the provider; a POST from a different authenticated subject approves it and terminates.
        write.MapPost("/providers/{id:guid}/terminate", async (Guid id, StateChange req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var actor = me.Principal?.Subject;
            if (string.IsNullOrWhiteSpace(actor)) return Results.Unauthorized();

            var (p, tenant) = await Load(db, id, me, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
            if (p.Status == ProviderStatus.Terminated)
                return Results.Problem(statusCode: 409, title: "already terminated", type: "urn:hbmp:provider-already-terminated");

            var pending = await db.TerminationRequests.FirstOrDefaultAsync(
                r => r.ProviderId == id && r.Status == TerminationRequestStatus.Requested, ct);

            // ---- First leg: open the request. The provider is untouched until somebody else agrees. ----
            if (pending is null)
            {
                if (string.IsNullOrWhiteSpace(req.Reason))
                    return Results.Problem(statusCode: 400, title: "a reason is required");

                var opened = new ProviderTerminationRequest
                {
                    RequestId = Guid.NewGuid(), TenantId = tenant!, ProviderId = id,
                    Reason = req.Reason, RequestedBy = actor, RequestedAt = clock.GetUtcNow(),
                };
                await using var openTx = await db.Database.BeginTransactionAsync(ct);
                db.TerminationRequests.Add(opened);
                await db.SaveChangesAsync(ct);
                await audit.EmitAsync(Draft(p, AuditAction.StateChange, me, tenant,
                    outcome: "TerminationRequested", reason: req.Reason), ct);
                await openTx.CommitAsync(ct);

                return Results.Accepted($"/api/v1/providers/{id}", new
                {
                    p.ProviderId, status = p.Status.ToString(),
                    termination = new { opened.RequestId, status = opened.Status.ToString(), opened.RequestedBy },
                    detail = "Termination requested. A different user must repeat this call to approve it.",
                });
            }

            // ---- Second leg: approve and terminate, but only under a DIFFERENT token. ----
            if (string.Equals(pending.RequestedBy, actor, StringComparison.Ordinal))
                return Results.Problem(statusCode: 422, title: "terminate is dual-controlled",
                    type: "urn:hbmp:dual-control-required",
                    detail: "This termination was requested by you; a different user must approve it.");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            pending.Status = TerminationRequestStatus.Approved;
            pending.ApprovedBy = actor;
            pending.ApprovedAt = clock.GetUtcNow();
            p.Status = ProviderStatus.Terminated;
            p.OnboardingState = OnboardingState.Terminated;
            p.UpdatedAt = clock.GetUtcNow();
            var revoked = await RevokeUsers(db, id, clock, ct);
            await db.SaveChangesAsync(ct);
            // Both subjects, and both are now facts the system observed rather than one it was told.
            await audit.EmitAsync(Draft(p, AuditAction.StateChange, me, tenant, outcome: "Terminated",
                reason: $"{pending.Reason} (requested by: {pending.RequestedBy}; approved by: {actor})"), ct);
            await outbox.EnqueueAsync("ProviderStatusChanged", "provider.events", new { providerId = p.ProviderId, status = "Terminated", tenantId = tenant }, ct);
            await outbox.EnqueueAsync("ProviderUsersRevoked", "provider.events", new { providerId = p.ProviderId, count = revoked, reason = "provider-terminated", tenantId = tenant }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { p.ProviderId, status = p.Status.ToString(), usersRevoked = revoked });
        });

        // --- Provision a provider-scoped user (SoD: no self-elevation / no clinical / no cross-provider) ----
        write.MapPost("/providers/{id:guid}/users", async (Guid id, ProvisionUser req, ProviderDbContext db, IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var (p, tenant) = await Load(db, id, me, ct);
            if (p is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var actorRoles = me.Principal?.Roles ?? new HashSet<string>();
            var sod = ProviderUserRules.CanProvision(actorRoles, req.Role);
            if (!sod.Allowed)
            {
                await audit.EmitAsync(new AuditEventDraft { EntityType = "provider_user", EntityId = req.SubjectRef, Action = AuditAction.Grant, ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = id.ToString(), DecisionOutcome = "Deny", DecisionReasonCode = "sod", Severity = AuditSeverity.Warning }, ct);
                return Results.Problem(statusCode: 403, title: "provisioning denied", detail: sod.Reason);
            }

            var user = new ProviderUser
            {
                UserId = Guid.NewGuid(), ProviderId = id, TenantId = tenant!, SubjectRef = req.SubjectRef,
                Role = req.Role, Status = ProviderUserStatus.Active, CreatedAt = clock.GetUtcNow(),
            };
            // 24.3 — identity provisioning is driven off ProviderUserProvisioned. A user row without its
            // event is an account that exists here and nowhere anyone can sign in with.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Users.Add(user);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Problem(statusCode: 409, title: "a user with this subject already exists in the tenant"); }
            await audit.EmitAsync(new AuditEventDraft { EntityType = "provider_user", EntityId = user.UserId.ToString(), Action = AuditAction.Create, ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = id.ToString(), DecisionOutcome = "provisioned" }, ct);
            // identity-service (Keycloak) provisioning is driven off this event (deferred sync).
            await outbox.EnqueueAsync("ProviderUserProvisioned", "provider.events", new { userId = user.UserId, providerId = id, user.SubjectRef, user.Role, tenantId = tenant }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/providers/{id}/users/{user.UserId}", new { user.UserId, user.Role });
        });

        // --- Credential-expiry reminder sweep → ProviderCredentialExpiring (FR-NET-007) --------------------
        write.MapPost("/providers/credentials/reminder-run", async (ProviderDbContext db, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock, IBusinessCalendar calendar, int? windowDays, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            var today = Today(calendar);
            var window = windowDays ?? 30;
            var creds = await db.Credentials.AsNoTracking().Where(c => c.TenantId == tenant && !c.IsDeleted && c.ValidTo != null).ToListAsync(ct);
            var due = creds.Where(c => CredentialRules.ExpiryReminderDue(c, today, window)).ToList();
            // 24.3 — the sweep is all-or-nothing: a half-emitted run reports a reminder count that does
            // not match the reminders anyone received.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var c in due)
                await outbox.EnqueueAsync("ProviderCredentialExpiring", "provider.events", new { credentialId = c.CredentialId, providerId = c.ProviderId, c.CredentialType, validTo = c.ValidTo, tenantId = tenant }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { evaluated = creds.Count, remindersEmitted = due.Count, windowDays = window });
        });
    }

    private static DateOnly Today(IBusinessCalendar calendar) => calendar.Today();   // 18.A3

    private static async Task<(Domain.Provider? provider, string? tenant)> Load(ProviderDbContext db, Guid id, IHbmpPrincipalAccessor me, CancellationToken ct)
    {
        var tenant = me.Principal?.TenantId;
        var p = await db.Providers
            .Include(x => x.Locations).Include(x => x.Credentials).Include(x => x.Contracts)
            .FirstOrDefaultAsync(x => x.ProviderId == id && x.TenantId == tenant && !x.IsDeleted, ct);
        return (p, tenant);
    }

    private static OnboardingWorkflow.Readiness ReadinessOf(Domain.Provider p, DateOnly on) => new(
        HasPrimaryLocation: p.Locations.Any(l => l.IsPrimary && !l.IsDeleted),
        HasMandatoryCredentials: p.Credentials.Any(c => c.IsMandatory && !c.IsDeleted),
        MandatoryCredentialsValid: CredentialRules.MandatoryCredentialsSatisfied(p.Credentials, on),
        HasActiveContract: p.Contracts.Any(c => ContractRules.InEffect(c, on)));

    private static async Task<int> RevokeUsers(ProviderDbContext db, Guid providerId, TimeProvider clock, CancellationToken ct)
    {
        var users = await db.Users.Where(u => u.ProviderId == providerId && u.Status == ProviderUserStatus.Active).ToListAsync(ct);
        foreach (var u in users) { u.Status = ProviderUserStatus.Revoked; u.RevokedAt = clock.GetUtcNow(); }
        return users.Count;
    }

    private static AuditEventDraft Draft(Domain.Provider p, AuditAction action, IHbmpPrincipalAccessor me, string? tenant, string? outcome = null, string? reason = null) => new()
    {
        EntityType = "provider", EntityId = p.ProviderId.ToString(), Action = action,
        ActorUserId = me.Principal?.Subject, TenantId = tenant, ProviderId = p.ProviderId.ToString(),
        DecisionOutcome = outcome, DecisionReasonCode = reason,
    };
}
