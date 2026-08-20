using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Patient.Domain;
using Mersal.Patient.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Patient.Api;

/// <summary>One intake row, as the bulk engine sends it.</summary>
public sealed record IntakeRequest(
    string CardNumber,
    string GivenName,
    string? MiddleName,
    string FamilyName,
    string Sex,
    string NationalityCode,
    string Phone,
    DateOnly? BirthDate,
    string? IndividualNo,
    string? CaseNo,
    /// <summary>Active, Suspended or Closed for a migration of historical members; null leaves the person
    /// Pending, which is what a file of new arrivals means.</summary>
    string? Status,
    IReadOnlyList<RegistrationNoteDto>? Notes,
    /// <summary>The coverage the row elects, stored as an intent exactly as the form's is — so an imported
    /// member who is approved later gets their coverage from the same code path as one registered at the
    /// desk, rather than a card number and nothing behind it.</summary>
    EnrolmentIntentDto? Enrolment);

public sealed record IntakeResult(Guid BeneficiaryId, string Status, string? MemberNo, bool Created, bool Changed);

/// <summary>
/// Phase 21 — register-or-update a beneficiary by CARD NUMBER.
///
/// <para><b>Why this exists.</b> A ten-thousand-row intake file is corrected and re-uploaded; that is the
/// normal case, not the exception. Without an upsert the second upload either creates ten thousand duplicate
/// people or must be hand-edited down to the rows that changed — and hand-editing an intake file is how one
/// member ends up with two subtly different records, two card numbers and two sets of limits.</para>
///
/// <para><b>Why the card number is the key.</b> It is the business key the whole operational record already
/// turns on, it is unique among non-deleted rows by index, and it is the one value the operator building the
/// file is certain to have in front of them. An identity document would be a better key in principle and a
/// worse one in practice: this population's documents change, expire and are re-issued.</para>
///
/// <para><b>What it will not do.</b> It never moves a card between people. If the card is held and the name on
/// the row is a different person, that is a conflict for a human, not something to resolve by overwriting
/// somebody's identity — so the row fails and says whose card it is.</para>
/// </summary>
public static class BeneficiaryIntakeEndpoints
{
    public static void MapBeneficiaryIntake(this IEndpointRouteBuilder app)
    {
        // patient:write, like every other beneficiary mutation. The bulk engine calls this AS THE OPERATOR
        // whose file it is, forwarding their bearer — so a caller who could not register one person by hand
        // cannot register ten thousand through the engine either.
        app.MapPut("/api/v1/beneficiaries/by-card", async (
                IntakeRequest req, PatientDbContext db, BeneficiaryRegistrar registrar,
                IAuditClient audit, IOutbox outbox, IHbmpPrincipalAccessor me, TimeProvider clock,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.CardNumber))
                    return Results.Problem(statusCode: 400, title: "cardNumber is required");

                var card = PersonFieldValidation.NormalizeCardNumber(req.CardNumber);
                var actor = me.Principal?.Subject;
                var now = clock.GetUtcNow();

                var existing = await db.Beneficiaries
                    .Include(x => x.Contacts)
                    .FirstOrDefaultAsync(x => x.CardNumber == card && !x.IsDeleted, ct);

                // ── New person ──────────────────────────────────────────────────────────────────────────
                if (existing is null)
                {
                    var domain = new RegisterBeneficiaryRequest(
                        req.GivenName, req.FamilyName, req.BirthDate, req.Sex, req.NationalityCode,
                        [],   // an intake file carries no identity document; the card IS the reference
                        string.IsNullOrWhiteSpace(req.Phone) ? [] : [new NewContact(ContactType.Phone, req.Phone, IsPrimary: true)])
                    {
                        CardNumber = card,
                        MiddleName = req.MiddleName,
                        IndividualNo = req.IndividualNo,
                        CaseNo = req.CaseNo,
                        // The bulk row elects its own coverage from its plan and tier columns immediately
                        // after this call. Passing a placeholder would be worse than passing none: it would
                        // look like a choice somebody made.
                        Enrolment = null,
                        // Narrows exactly two checks — the identity document and the elected plan. Every other
                        // rule the form is held to applies here unchanged (see RegisterBeneficiaryRequest).
                        IsIntake = true,
                    };

                    // Reported verbatim: the operator fixes a cell and re-uploads the file.
                    var result = await registrar.RegisterAsync(domain, actor, ct);
                    switch (result)
                    {
                        case RegistrationResult.Invalid invalid:
                            return Results.ValidationProblem(invalid.Errors.ToDictionary(e => e, e => new[] { e }));
                        case RegistrationResult.DuplicateCardNumber dup:
                            return Conflict(dup.ExistingBeneficiaryId, card);
                        case RegistrationResult.DuplicateIdentifier dupId:
                            return Conflict(dupId.ExistingBeneficiaryId, card);
                    }
                    if (result is not RegistrationResult.Created created)
                        return Results.Problem(statusCode: 500, title: "unexpected");

                    var person = created.Beneficiary;
                    ApplyStatus(person, req.Status);
                    // 24.3 — intake creates the person, their registration and the event that tells the
                    // rest of the platform they exist. All of it, or none of it.
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    db.Beneficiaries.Add(person);

                    var registrationId = Guid.NewGuid();
                    db.Registrations.Add(new Registration
                    {
                        RegistrationId = registrationId, BeneficiaryId = person.BeneficiaryId,
                        TenantId = person.TenantId, Status = RegistrationStatus.Pending,
                        CoverageBound = req.Enrolment is not null,
                        // The bulk engine runs AS the operator whose file it is, so the applications a file
                        // creates are filed by that operator — and a request for more information on one of
                        // them reaches the person who uploaded it, not a queue with no owner.
                        CreatedBy = actor, CreatedByName = me.Principal?.DisplayName,
                        CreatedAt = now, UpdatedAt = now,
                    });
                    WriteIntent(db, registrationId, person.TenantId, req.Enrolment, now, existing: null);
                    WriteNotes(db, registrationId, person.TenantId, req.Notes, now, existing: []);

                    await db.SaveChangesAsync(ct);
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "beneficiary", EntityId = person.BeneficiaryId.ToString(),
                        Action = AuditAction.Create, ActorUserId = actor,
                        DecisionOutcome = "IntakeCreated", FieldClasses = ["identity", "pii"],
                    }, ct);
                    await outbox.EnqueueAsync("BeneficiaryRegistered", "patient.events", new
                    {
                        tenantId = person.TenantId, beneficiaryId = person.BeneficiaryId,
                        status = person.Status.ToString(), cardNumber = person.CardNumber,
                        givenName = person.GivenName, middleName = person.MiddleName, familyName = person.FamilyName,
                        primaryPhone = person.Contacts.FirstOrDefault()?.Value,
                        identifiers = Array.Empty<object>(),
                    }, ct);

                    await tx.CommitAsync(ct);
                    return Results.Ok(new IntakeResult(
                        person.BeneficiaryId, person.Status.ToString(), person.MemberNo, Created: true, Changed: true));
                }

                // ── Existing card ───────────────────────────────────────────────────────────────────────
                //
                // A card is never moved between people. If the names disagree this is either a mis-keyed card
                // or a card re-issued without the old one being retired, and both need a human — silently
                // overwriting one person's identity with another's is the one outcome with no way back.
                if (!SameName(existing, req))
                    return Conflict(existing.BeneficiaryId, card, nameMismatch: true);

                var before = Snapshot(existing);
                // The elected coverage counts as a change too: correcting one member's contribution is the
                // most common reason a file is re-uploaded, and reporting that row as "unchanged" would tell
                // the operator their correction had not been taken.
                string? intentBefore = null, intentAfter = null;
                existing.GivenName = req.GivenName.Trim();
                existing.MiddleName = string.IsNullOrWhiteSpace(req.MiddleName) ? null : req.MiddleName.Trim();
                existing.FamilyName = req.FamilyName.Trim();
                existing.BirthDate = req.BirthDate ?? existing.BirthDate;
                existing.Sex = req.Sex ?? existing.Sex;
                existing.NationalityCode = req.NationalityCode?.Trim().ToUpperInvariant() ?? existing.NationalityCode;
                existing.IndividualNo = string.IsNullOrWhiteSpace(req.IndividualNo) ? existing.IndividualNo : req.IndividualNo.Trim();
                existing.CaseNo = string.IsNullOrWhiteSpace(req.CaseNo) ? existing.CaseNo : req.CaseNo.Trim();
                ApplyStatus(existing, req.Status);

                if (!string.IsNullOrWhiteSpace(req.Phone))
                {
                    var primary = existing.Contacts.FirstOrDefault(c => c.ContactType == ContactType.Phone && !c.IsDeleted);
                    if (primary is null)
                    {
                        db.Contacts.Add(new Contact
                        {
                            ContactId = Guid.NewGuid(), BeneficiaryId = existing.BeneficiaryId,
                            TenantId = existing.TenantId, ContactType = ContactType.Phone,
                            Value = req.Phone.Trim(), IsPrimary = true,
                        });
                    }
                    else primary.Value = req.Phone.Trim();
                }

                var registration = await db.Registrations.AsNoTracking()
                    .Where(r => r.BeneficiaryId == existing.BeneficiaryId)
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
                if (registration is not null)
                {
                    // Fetched once for the registration rather than once per slot: six synchronous round trips
                    // per row is six thousand on a thousand-row file.
                    var onFile = await db.RegistrationNotes
                        .Where(n => n.RegistrationId == registration.RegistrationId).ToListAsync(ct);
                    WriteNotes(db, registration.RegistrationId, existing.TenantId, req.Notes, now, onFile);

                    var intentOnFile = await db.EnrolmentIntents
                        .FirstOrDefaultAsync(i => i.RegistrationId == registration.RegistrationId, ct);
                    intentBefore = Snapshot(intentOnFile);
                    WriteIntent(db, registration.RegistrationId, existing.TenantId, req.Enrolment, now, intentOnFile);
                    intentAfter = Snapshot(intentOnFile ?? db.EnrolmentIntents.Local
                        .FirstOrDefault(i => i.RegistrationId == registration.RegistrationId));
                }

                var after = Snapshot(existing);
                var changed = !string.Equals(before, after, StringComparison.Ordinal)
                              || !string.Equals(intentBefore, intentAfter, StringComparison.Ordinal);
                if (changed)
                {
                    existing.UpdatedBy = actor;
                    existing.UpdatedAt = now;
                }
                await db.SaveChangesAsync(ct);

                // A no-op re-upload is still a read of the record, but it is not a CHANGE — auditing it as one
                // would bury the rows that did change under ten thousand that did not.
                if (changed)
                {
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "beneficiary", EntityId = existing.BeneficiaryId.ToString(),
                        Action = AuditAction.Update, ActorUserId = actor,
                        DecisionOutcome = "IntakeUpdated", BeforeState = before, AfterState = after,
                        FieldClasses = ["identity", "pii"],
                    }, ct);
                }

                return Results.Ok(new IntakeResult(
                    existing.BeneficiaryId, existing.Status.ToString(), existing.MemberNo,
                    Created: false, Changed: changed));
            })
            .RequireAuthorization(HbmpPolicies.Scope("patient:write"))
        .Produces<IntakeResult>();
    }

    /// <summary>
    /// The file's status column, mapped onto the lifecycle.
    ///
    /// <para>"Closed" is the operational word for what the state machine calls Inactive; the sheet has always
    /// said Closed and the enum has always said Inactive, and translating here is better than adding a seventh
    /// state that means the same as one we have. Anything the machine does not recognise leaves the status
    /// alone rather than guessing — a typo in one cell must not deactivate a member.</para>
    /// </summary>
    private static void ApplyStatus(Beneficiary person, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return;
        var target = status.Trim().ToLowerInvariant() switch
        {
            "active" => BeneficiaryStatus.Active,
            "suspended" => BeneficiaryStatus.Suspended,
            "closed" or "inactive" => BeneficiaryStatus.Inactive,
            _ => (BeneficiaryStatus?)null,
        };
        // Still routed through the lifecycle: an intake file is not a way around the state machine, and a
        // move the machine refuses (Blocked → anything, say) leaves the record as it was.
        if (target is { } to && BeneficiaryLifecycle.CanTransition(person.Status, to)) person.Status = to;
    }

    /// <summary>Record or correct the elected coverage. A re-upload that changes the contribution is the most
    /// common correction an operator makes, so an existing intent is UPDATED rather than left alone.</summary>
    private static void WriteIntent(
        PatientDbContext db, Guid registrationId, string tenantId,
        EnrolmentIntentDto? intent, DateTimeOffset now, EnrolmentIntent? existing)
    {
        if (intent is null) return;
        if (existing is not null)
        {
            existing.PlanId = intent.PlanId;
            existing.NetworkTierId = intent.NetworkTierId;
            existing.ContributionPercent = intent.ContributionPercent;
            existing.DefaultBranchId = intent.DefaultBranchId;
            existing.UpdatedAt = now;
            return;
        }
        db.EnrolmentIntents.Add(new EnrolmentIntent
        {
            RegistrationId = registrationId, TenantId = tenantId,
            PlanId = intent.PlanId, NetworkTierId = intent.NetworkTierId,
            ContributionPercent = intent.ContributionPercent, DefaultBranchId = intent.DefaultBranchId,
            CreatedAt = now, UpdatedAt = now,
        });
    }

    /// <summary>
    /// Fill or update the slots the row names, leaving the others alone.
    ///
    /// <para>A slot the file OMITS is not a slot the file cleared. An intake sheet routinely carries only the
    /// columns the sending partner tracks, and treating "absent" as "empty" would wipe a diagnosis somebody
    /// recorded at the desk on the next routine re-upload.</para>
    /// </summary>
    private static void WriteNotes(
        PatientDbContext db, Guid registrationId, string tenantId,
        IReadOnlyList<RegistrationNoteDto>? notes, DateTimeOffset now, IReadOnlyList<RegistrationNote> existing)
    {
        foreach (var note in notes ?? [])
        {
            if (string.IsNullOrWhiteSpace(note.Value) || RegistrationNoteSlots.For(note.Slot) is null) continue;
            var current = existing.FirstOrDefault(n => n.Slot == note.Slot);
            if (current is not null)
            {
                current.Value = note.Value.Trim();
                current.UpdatedAt = now;
                continue;
            }
            db.RegistrationNotes.Add(new RegistrationNote
            {
                RegistrationId = registrationId, TenantId = tenantId, Slot = note.Slot,
                Value = note.Value.Trim(),
                // From the slot, never the request — see RegistrationNoteSlots.
                Visibility = RegistrationNoteSlots.VisibilityOf(note.Slot),
                CreatedAt = now, UpdatedAt = now,
            });
        }
    }

    /// <summary>Given + family only, case-insensitive and trimmed. A middle name that appears on one file and
    /// not the next is an ordinary transcription difference, not evidence of a different person.</summary>
    private static bool SameName(Beneficiary person, IntakeRequest req) =>
        string.Equals(person.GivenName?.Trim(), req.GivenName?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(person.FamilyName?.Trim(), req.FamilyName?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The intent as a comparable string. The contribution is formatted to a FIXED two decimals
    /// because that is the column's precision: Postgres returns 10.00 and a file says 10, and comparing the
    /// default renderings of those two makes an unchanged row report as changed on every single re-upload —
    /// which would turn "skipped 998, applied 2" into "applied 1000" and destroy the one number the operator
    /// reads to see what their correction actually did.</summary>
    private static string? Snapshot(EnrolmentIntent? i) =>
        i is null
            ? null
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{i.PlanId}|{i.NetworkTierId}|{i.ContributionPercent:F2}|{i.DefaultBranchId}");

    private static string Snapshot(Beneficiary b) =>
        $"{b.GivenName}|{b.MiddleName}|{b.FamilyName}|{b.BirthDate}|{b.Sex}|{b.NationalityCode}|{b.IndividualNo}|{b.CaseNo}|{b.Status}";

    private static IResult Conflict(Guid existingId, string card, bool nameMismatch = false) =>
        Results.Problem(statusCode: 409,
            title: nameMismatch ? "card-held-by-another-person" : "duplicate-card-number",
            detail: nameMismatch
                ? $"card '{card}' is held by beneficiary {existingId} under a different name; a card is never moved between people by an import"
                : $"card '{card}' is already held by beneficiary {existingId}",
            type: nameMismatch ? "urn:hbmp:card-held-by-another-person" : "urn:hbmp:duplicate-card-number",
            extensions: new Dictionary<string, object?> { ["existingBeneficiaryId"] = existingId });
}
