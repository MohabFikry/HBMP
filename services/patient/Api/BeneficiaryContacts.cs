using Mersal.Audit.Client;
using Mersal.Events;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Patient.Domain;
using Mersal.Patient.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Patient.Api;

/// <summary>
/// Phase 19.5b — read and upsert a beneficiary's contact details.
///
/// <para>The bulk engine's ContactUpdate job needs a write path for contacts, and patient-service OWNS
/// contacts. Adding it here rather than reaching into the patient schema from policy-service is the same call
/// 19.5 made for names: the service that knows what a field means is the one that decides who may change it,
/// audits the change, and publishes the event other services react to.</para>
///
/// <para>UPSERT BY (type, primary), not blind append. A bulk file of corrected phone numbers must not leave
/// every member with two phone numbers and no way to tell which one anybody should ring.</para>
/// </summary>
public static class BeneficiaryContactEndpoints
{
    public static void MapBeneficiaryContacts(this IEndpointRouteBuilder app)
    {
        // READ — needed by the bulk engine to snapshot what a contact looked like BEFORE a row changed it, so
        // rollback is a restore rather than a delete.
        app.MapGet("/api/v1/beneficiaries/{id:guid}/contacts", async (
            Guid id, PatientDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return Results.Unauthorized();

            var beneficiary = await db.Beneficiaries.AsNoTracking().Include(b => b.Contacts)
                .FirstOrDefaultAsync(b => b.BeneficiaryId == id && !b.IsDeleted, ct);
            if (beneficiary is null)
                return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary", EntityId = id.ToString(), Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId, DecisionOutcome = "contacts", FieldClasses = ["pii"],
            }, ct);

            return Results.Ok(beneficiary.Contacts.Where(c => !c.IsDeleted).Select(c => new ContactView(
                c.ContactId, c.ContactType.ToString(), c.Value, c.PreferredChannel, c.IsPrimary)));
        }).RequireAuthorization(HbmpPolicies.Scope("patient:read"));

        app.MapPut("/api/v1/beneficiaries/{id:guid}/contacts", async (
            Guid id, UpsertContact req, PatientDbContext db, IHbmpPrincipalAccessor me,
            IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return Results.Unauthorized();
            if (req is null || string.IsNullOrWhiteSpace(req.Value))
                return Results.Problem(statusCode: 400, title: "a contact value is required");
            if (!Enum.TryParse<ContactType>(req.ContactType, ignoreCase: true, out var type))
                return Results.Problem(statusCode: 400, title: $"'{req.ContactType}' is not a contact type");

            var beneficiary = await db.Beneficiaries.Include(b => b.Contacts)
                .FirstOrDefaultAsync(b => b.BeneficiaryId == id && !b.IsDeleted, ct);
            if (beneficiary is null)
                return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var live = beneficiary.Contacts.Where(c => !c.IsDeleted).ToList();
            var existing = live.FirstOrDefault(c => c.ContactType == type && (req.IsPrimary ? c.IsPrimary : true));
            var before = existing is null ? null : new ContactView(
                existing.ContactId, existing.ContactType.ToString(), existing.Value, existing.PreferredChannel, existing.IsPrimary);

            if (existing is null)
            {
                existing = new Contact
                {
                    ContactId = Guid.NewGuid(), TenantId = beneficiary.TenantId, BeneficiaryId = id,
                    ContactType = type,
                };
                beneficiary.Contacts.Add(existing);
            }
            existing.Value = req.Value.Trim();
            existing.PreferredChannel = req.PreferredChannel;

            // At most one primary per type. Demoting the incumbent is part of the same transaction, because a
            // record with two primary phone numbers is one nobody can act on.
            if (req.IsPrimary)
            {
                foreach (var other in live.Where(c => c.ContactType == type && c.ContactId != existing.ContactId))
                    other.IsPrimary = false;
                existing.IsPrimary = true;
            }

            beneficiary.UpdatedAt = clock.GetUtcNow();
            beneficiary.UpdatedBy = principal.Subject;
            // 24.3 — the contact and BeneficiaryContactChanged commit together; a lost event leaves
            // notification dialling the old number.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary", EntityId = id.ToString(), Action = AuditAction.Update,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = before is null ? "contact-added" : "contact-updated",
                DecisionReasonCode = type.ToString(), FieldClasses = ["pii"],
            }, ct);
            await outbox.EnqueueAsync("BeneficiaryContactChanged", "patient.events", new
            {
                tenantId = beneficiary.TenantId, beneficiaryId = id, contactId = existing.ContactId,
                contactType = type.ToString(), isPrimary = existing.IsPrimary,
            }, ct);

            await tx.CommitAsync(ct);
            return Results.Ok(new ContactUpsertView(
                existing.ContactId, type.ToString(), existing.Value, existing.PreferredChannel, existing.IsPrimary, before));
        }).RequireAuthorization(HbmpPolicies.Scope("patient:write"));
    }
}

public sealed record UpsertContact(string ContactType, string Value, bool IsPrimary = false, string? PreferredChannel = null);

public sealed record ContactView(Guid ContactId, string ContactType, string Value, string? PreferredChannel, bool IsPrimary);

/// <summary>The upsert echoes what the contact looked like BEFORE — which is what makes a bulk contact update
/// reversible without policy-service having to guess at patient-service's prior state.</summary>
public sealed record ContactUpsertView(
    Guid ContactId, string ContactType, string Value, string? PreferredChannel, bool IsPrimary, ContactView? Previous);
