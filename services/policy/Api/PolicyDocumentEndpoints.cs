using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

// Phase 19.3b — documents on policy and member (design 38 §5b).

public sealed record AttachDocumentRequest(
    string DocumentClass, string Title, string? Description, DateOnly? DocumentDate,
    string? IssuingProvider, string? VisibilityClass, string? SensitiveCategory, DateOnly? ExpiresOn,
    /// <summary>Set when this replaces an earlier version of the same logical document.</summary>
    Guid? SupersedesLinkId);

public sealed record WithdrawDocument(string Reason);
public sealed record VerifyDocument(string? Note);

/// <summary>Metadata only — never a URL. Downloading is a separate, scoped, always-audited act, so a list
/// response that carried a link would hand out the content to everyone who can see the list.</summary>
public sealed record PolicyDocumentView(
    Guid LinkId, string Scope, Guid ScopeRef, Guid DocumentId, int VersionNo, Guid? SupersedesLinkId,
    string DocumentClass, string VisibilityClass, string? SensitiveCategory,
    string Title, string? Description, DateOnly? DocumentDate, string? IssuingProvider,
    string UploadedByUsername, string UploadedByDisplay, DateTimeOffset UploadedAt,
    string Status, string? WithdrawnByUsername, DateTimeOffset? WithdrawnAt, string? WithdrawalReason,
    DateOnly? ExpiresOn, bool Expired, string? VerifiedByUsername, DateTimeOffset? VerifiedAt,
    /// <summary>Whether THIS caller may fetch the bytes — projected so the UI's affordance and the API's 403
    /// cannot disagree, and so a locked row renders as locked rather than as a broken link.</summary>
    bool CanDownload)
{
    public static PolicyDocumentView For(
        PolicyDocument d, IReadOnlyCollection<string> roles, DateOnly today, bool hasSensitiveGrant = false)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new(d.LinkId, d.Scope.ToString(), d.ScopeRef, d.DocumentId, d.VersionNo, d.SupersedesLinkId,
            d.DocumentClass.ToString(), d.VisibilityClass.ToString(), d.SensitiveCategory?.ToString(),
            d.Title, d.Description, d.DocumentDate, d.IssuingProvider,
            d.UploadedByUsername, d.UploadedByDisplay, d.UploadedAt,
            d.Status.ToString(), d.WithdrawnByUsername, d.WithdrawnAt, d.WithdrawalReason,
            d.ExpiresOn, d.IsExpired(today), d.VerifiedByUsername, d.VerifiedAt,
            DocumentAccess.MayDownload(d.DocumentClass, d.VisibilityClass, roles, hasSensitiveGrant));
    }
}

/// <summary>
/// Phase 19.3b — attach, list, download, withdraw and verify documents on a policy or a member.
///
/// <para><b>Listing and downloading are different authorities.</b> Everyone entitled to the record sees that a
/// document EXISTS — class, title, date, uploader, status — because a record that looks empty sends an officer
/// away believing nothing was filed. Fetching the CONTENT is narrower and always audited, through a short-TTL
/// signed URL minted per request.</para>
/// </summary>
public static class PolicyDocumentEndpoints
{
    /// <summary>Short enough that a leaked link is useless before it can be shared, long enough to survive a
    /// slow connection on a clinic's network.</summary>
    private static readonly TimeSpan DownloadTtl = TimeSpan.FromMinutes(2);

    public static void MapPolicyDocuments(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("policy:read"));
        var write = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("document:write"));

        MapAttach(write, "/policies/{id:guid}/documents", NoteScope.Policy);
        MapAttach(write, "/enrollments/{id:guid}/documents", NoteScope.Member);
        MapList(read, "/policies/{id:guid}/documents", NoteScope.Policy);
        MapList(read, "/enrollments/{id:guid}/documents", NoteScope.Member);
        MapDownload(read);
        MapIdentityPhoto(read);
        MapLifecycle(write);
    }

    // ---- Identity photo (phase 20.3) ---------------------------------------------------------------------

    /// <summary>
    /// Resolve the member's current identification photograph to a SHORT-TTL SIGNED URL.
    ///
    /// <para>Keyed on the beneficiary rather than a link id, because the caller (profile-service, on behalf of a
    /// receptionist) knows who the patient is and should not have to enumerate their documents to find a face.
    /// Enumerating documents is a wider read than looking at a photo, and asking for the wider one first would
    /// be exactly backwards.</para>
    ///
    /// <para>404 when there is no photo — including when consent was refused. That is an ordinary answer, not an
    /// error: the SPA renders initials, and nothing about care changes.</para>
    /// </summary>
    private static void MapIdentityPhoto(RouteGroupBuilder read)
    {
        read.MapGet("/beneficiaries/{beneficiaryId:guid}/identity-photo", async (
            Guid beneficiaryId, HttpRequest request, PolicyDbContext db, IDocumentStore store,
            IHbmpPrincipalAccessor me, IAuditClient audit, TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            // The photo's own allow-list, NARROWER than the administrative class it is filed under. Finance,
            // claims, labs, pharmacies and platform admins are refused here even though they may read other
            // administrative documents about the same member (design 39 §5).
            if (!Mersal.Authz.ProfilePhotoAccess.MayView(principal.Roles))
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "identity_photo", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
                    ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                    TenantId = principal.TenantId, DecisionOutcome = "denied",
                    DecisionReasonCode = "role-has-no-identification-need",
                    FieldClasses = ["identity"], Severity = AuditSeverity.Warning,
                }, ct);
                return GateResults.Forbidden("urn:hbmp:photo-access-denied",
                    detail: "Your role does not receive beneficiary photographs.",
                    reason: "role-has-no-identification-need");
            }

            var enrollmentIds = await db.Enrollments.AsNoTracking()
                .Where(e => e.BeneficiaryId == beneficiaryId && !e.IsDeleted)
                .Select(e => e.EnrollmentId).ToListAsync(ct);

            // Newest ACTIVE link wins. Superseded versions are retained (never silently overwritten) and are
            // simply not the current face.
            var photo = await db.PolicyDocuments.AsNoTracking()
                .Where(d => d.Scope == NoteScope.Member && enrollmentIds.Contains(d.ScopeRef)
                            && d.DocumentClass == DocumentClass.IdentityPhoto
                            && d.Status == DocumentLinkStatus.Active)
                .OrderByDescending(d => d.VersionNo).ThenByDescending(d => d.UploadedAt)
                .FirstOrDefaultAsync(ct);
            if (photo is null) return NotFound();

            // Minted AS THE CALLER — a service token would let the store hand out bytes the caller's own
            // authorization would have refused.
            var url = await store.SignedDownloadUrlAsync(
                photo.DocumentId, IdentityPhotoRules.SignedUrlTtl,
                request.Headers.Authorization.FirstOrDefault(), ct);
            if (url is null) return NotFound();

            // Every retrieval is audited. A photo read is the disclosure of a person's face to a named user at a
            // named time — precisely what a data-subject access request asks about.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "identity_photo", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId, Purpose = "identification",
                DecisionOutcome = "IdentityPhotoResolved", DecisionReasonCode = photo.LinkId.ToString(),
                FieldClasses = ["identity"], Severity = AuditSeverity.Notice,
            }, ct);

            // The injected clock rather than a bare wall-clock read: a TTL a test cannot pin is a TTL nobody
            // can prove expires, and "the signed URL expires" is an acceptance criterion of design 39 §5.
            return Results.Ok(new IdentityPhotoView(
                photo.LinkId, photo.VersionNo, url.ToString(),
                clock.GetUtcNow().Add(IdentityPhotoRules.SignedUrlTtl)));
        })
        .Produces<IdentityPhotoView>();
    }

    // ---- Attach ------------------------------------------------------------------------------------------
    private static void MapAttach(RouteGroupBuilder group, string route, NoteScope scope)
    {
        group.MapPost(route, async (Guid id, HttpRequest request, PolicyDbContext db, IDocumentStore store,
            IHbmpPrincipalAccessor me, IAuditClient audit, IOutbox outbox, TimeProvider clock,
            CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            if (!request.HasFormContentType) return ProblemResults.Invalid("MULTIPART_REQUIRED", "Upload a file as multipart/form-data.");

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0) return ProblemResults.Invalid("FILE_REQUIRED", "A file is required.");

            var req = Bind(form);
            if (!Enum.TryParse<DocumentClass>(req.DocumentClass, out var documentClass))
                return ProblemResults.Invalid("UNKNOWN_DOCUMENT_CLASS", $"'{req.DocumentClass}' is not a document class.");
            if (string.IsNullOrWhiteSpace(req.Title))
                return ProblemResults.Invalid("TITLE_REQUIRED", "A title is required.");

            SensitiveCategory? sensitive = null;
            if (!string.IsNullOrWhiteSpace(req.SensitiveCategory))
            {
                if (!Enum.TryParse<SensitiveCategory>(req.SensitiveCategory, out var parsed))
                    return ProblemResults.Invalid("UNKNOWN_SENSITIVE_CATEGORY", $"'{req.SensitiveCategory}' is not a sensitive category.");
                sensitive = parsed;
            }

            NoteVisibility? requested = null;
            if (!string.IsNullOrWhiteSpace(req.VisibilityClass))
            {
                if (!Enum.TryParse<NoteVisibility>(req.VisibilityClass, out var parsed))
                    return ProblemResults.Invalid("UNKNOWN_VISIBILITY_CLASS", $"'{req.VisibilityClass}' is not a visibility class.");
                requested = parsed;
            }

            // A finance user may attach an invoice to a member but must not file a past medical history:
            // clinical material entering the system with no clinical hand on it is both a data-quality problem
            // and a way to smuggle clinical content in under an administrative badge.
            if (!DocumentAccess.MayUpload(documentClass, principal.Roles))
                return GateResults.Forbidden("urn:hbmp:document-upload-denied",
                    detail: $"Your role may not upload {documentClass} documents.", reason: "class-not-permitted-for-role");

            // THE CLASSIFICATION RULE. The class (and any declared sensitive category) sets a floor; the
            // uploader may raise it and never lower it. A refusal names both values rather than silently
            // applying the floor — silently correcting them teaches uploaders that the field does nothing.
            var visibility = DocumentClassification.Resolve(documentClass, sensitive, requested);
            if (visibility is null)
                return ProblemResults.Unprocessable("VISIBILITY_BELOW_CLASS_DEFAULT",
                    $"A {documentClass} document defaults to " +
                    $"{DocumentClassification.DefaultFor(documentClass, sensitive)}; visibility may be raised but never lowered.");

            var exists = scope == NoteScope.Policy
                ? await db.Policies.AnyAsync(p => p.PolicyId == id && !p.IsDeleted, ct)
                : await db.Enrollments.AnyAsync(e => e.EnrollmentId == id && !e.IsDeleted, ct);
            if (!exists) return NotFound();

            // THE CONSENT GATE (phase 20.3, design 39 §5). An identification photograph is only stored once a
            // consent covering photography is on file. Enforced here, at the only door the bytes come through,
            // rather than in the UI — a rule the client checks is a rule the next client forgets.
            if (documentClass == DocumentClass.IdentityPhoto)
            {
                if (scope != NoteScope.Member)
                    return ProblemResults.Invalid("PHOTO_IS_MEMBER_SCOPED",
                        "An identification photograph belongs to a member, not to a policy.");

                var onFile = await db.PolicyDocuments.AsNoTracking()
                    .Where(d => d.Scope == NoteScope.Member && d.ScopeRef == id)
                    .ToListAsync(ct);
                if (!IdentityPhotoRules.ConsentSatisfied(onFile, BusinessCalendar.DateIn(clock.GetUtcNow())))
                    return ProblemResults.Unprocessable("PHOTO_CONSENT_REQUIRED", IdentityPhotoRules.ConsentMissing);
            }

            // Hand the bytes to document-service: MIME/size validation, FAIL-CLOSED ClamAV scan, checksum,
            // MinIO. Nothing is linked until it comes back clean.
            var beneficiaryId = scope == NoteScope.Member
                ? await db.Enrollments.Where(e => e.EnrollmentId == id).Select(e => e.BeneficiaryId).FirstAsync(ct)
                : id;
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var stored = await store.StoreAsync(
                beneficiaryId, documentClass.ToString(), file.ContentType, buffer.ToArray(),
                request.Headers.Authorization.FirstOrDefault(), ct);

            switch (stored)
            {
                case DocumentStoreResult.Quarantined q:
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "policy_document", EntityId = $"{scope}:{id}", Action = AuditAction.Create,
                        ActorUserId = principal.Subject, TenantId = principal.TenantId,
                        DecisionOutcome = "quarantined", DecisionReasonCode = q.Signature,
                        Severity = AuditSeverity.High,
                    }, ct);
                    return ProblemResults.Unprocessable("MALWARE_DETECTED",
                        "The file was quarantined by the malware scanner and nothing was linked.");
                case DocumentStoreResult.Rejected r:
                    return ProblemResults.Invalid("UPLOAD_REJECTED", r.Reason);
            }
            var ok = (DocumentStoreResult.Stored)stored;

            // A re-upload is a NEW VERSION. The prior link becomes Superseded and is never deleted — the
            // superseded version is what a dispute about "which report did you act on" is settled with.
            var versionNo = 1;
            // The prior link's supersede, the new link and the event announcing it commit together. The bytes
            // are already in object storage by this point and stay there on a rollback: an unreferenced blob is
            // reclaimable, whereas a link row with no event is a document the rest of the platform never saw.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (req.SupersedesLinkId is { } priorId)
            {
                var prior = await db.PolicyDocuments.FirstOrDefaultAsync(d => d.LinkId == priorId, ct);
                if (prior is null) return ProblemResults.Invalid("UNKNOWN_SUPERSEDED_LINK", "That document link does not exist.");
                if (prior.Status == DocumentLinkStatus.Withdrawn)
                    return ProblemResults.Conflict("SUPERSEDES_WITHDRAWN",
                        "A withdrawn document cannot be superseded — attach a new one instead.");
                versionNo = prior.VersionNo + 1;
                prior.Status = DocumentLinkStatus.Superseded;
                prior.UpdatedAt = clock.GetUtcNow();
            }

            var now = clock.GetUtcNow();
            var link = new PolicyDocument
            {
                LinkId = Guid.NewGuid(), Scope = scope, ScopeRef = id,
                DocumentId = ok.DocumentId, VersionNo = versionNo, SupersedesLinkId = req.SupersedesLinkId,
                DocumentClass = documentClass, VisibilityClass = visibility.Value, SensitiveCategory = sensitive,
                Title = req.Title.Trim(), Description = req.Description,
                DocumentDate = req.DocumentDate, IssuingProvider = req.IssuingProvider,
                UploadedByUserId = SubjectId(principal) ?? Guid.Empty,
                UploadedByUsername = principal.Subject,
                UploadedByDisplay = string.IsNullOrWhiteSpace(principal.DisplayName) ? principal.Subject : principal.DisplayName!,
                UploadedAt = now, Status = DocumentLinkStatus.Active, ExpiresOn = req.ExpiresOn,
                CreatedAt = now, UpdatedAt = now,
            };
            db.PolicyDocuments.Add(link);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "policy_document", EntityId = link.LinkId.ToString(), Action = AuditAction.Create,
                ActorUserId = principal.Subject, TenantId = principal.TenantId,
                DecisionOutcome = "attached",
                FieldClasses = [visibility.Value.ToString().ToLowerInvariant()],
            }, ct);
            await outbox.EnqueueAsync(
                req.SupersedesLinkId is null ? "DocumentAttached" : "DocumentSuperseded", "policy.events", new
                {
                    tenantId = link.TenantId, linkId = link.LinkId, scope = scope.ToString(), scopeRef = id,
                    documentClass = documentClass.ToString(), visibilityClass = visibility.Value.ToString(),
                    versionNo, supersedes = req.SupersedesLinkId,
                }, ct);
            await tx.CommitAsync(ct);

            return Results.Created($"/api/v1/documents/{link.LinkId}",
                PolicyDocumentView.For(link, principal.Roles, BusinessCalendar.DateIn(now)));
        }).DisableAntiforgery();
    }

    // ---- List (metadata only) ----------------------------------------------------------------------------
    private static void MapList(RouteGroupBuilder group, string route, NoteScope scope)
    {
        group.MapGet(route, async (Guid id, string? @class, string? status, DateOnly? from, DateOnly? to,
            PolicyDbContext db, IHbmpPrincipalAccessor me, IBusinessCalendar calendar, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var q = db.PolicyDocuments.AsNoTracking().Where(d => d.Scope == scope && d.ScopeRef == id);
            if (@class is not null && Enum.TryParse<DocumentClass>(@class, out var c)) q = q.Where(d => d.DocumentClass == c);
            if (status is not null && Enum.TryParse<DocumentLinkStatus>(status, out var s)) q = q.Where(d => d.Status == s);
            if (from is { } f) q = q.Where(d => d.DocumentDate >= f);
            if (to is { } t) q = q.Where(d => d.DocumentDate <= t);

            // document_date FIRST. Past medical history reads in clinical order — a 2019 discharge summary
            // scanned in today belongs in 2019 on the member's history, not at the top.
            var rows = await q
                .OrderByDescending(d => d.DocumentDate ?? DateOnly.MinValue)
                .ThenByDescending(d => d.UploadedAt)
                .ToListAsync(ct);

            var today = calendar.Today();
            // Metadata only — no bytes and, deliberately, no URL. A list that carried links would hand the
            // content to everyone who can see the list, which is precisely the wider audience.
            return Results.Ok(rows.Select(d => PolicyDocumentView.For(d, principal.Roles, today)));
        });
    }

    // ---- Download (separate, scoped, always audited) ------------------------------------------------------
    private static void MapDownload(RouteGroupBuilder read)
    {
        read.MapGet("/documents/{linkId:guid}/download", async (Guid linkId, string? purpose,
            HttpRequest request, PolicyDbContext db, IDocumentStore store, IHbmpPrincipalAccessor me,
            IAuditClient audit, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            // The signed URL is minted AS THE CALLER — a service token would let the store hand out bytes the
            // caller's own authorization would have refused.
            var bearer = request.Headers.Authorization.FirstOrDefault();

            var link = await db.PolicyDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.LinkId == linkId, ct);
            if (link is null) return NotFound();

            // Restricted material is existence-only until released through the design-37 §6 request/grant
            // flow. No role reaches it here — inventing a parallel unlock would be a side channel around the
            // one mechanism that exists.
            // Class-aware: an IdentityPhoto is Administrative by visibility but carries its own, much narrower
            // allow-list (design 39 §5), so the visibility class alone would hand a refugee's photograph to
            // finance and claims.
            var mayDownload = DocumentAccess.MayDownload(
                link.DocumentClass, link.VisibilityClass, principal.Roles, hasSensitiveGrant: false);
            if (!mayDownload)
            {
                // A DENIED download is audited too. Someone reaching for a clinical record they may not read
                // is exactly what a review looks for, and a silent 403 leaves it nowhere.
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "policy_document", EntityId = linkId.ToString(), Action = AuditAction.Read,
                    ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                    TenantId = principal.TenantId, DecisionOutcome = "denied",
                    DecisionReasonCode = link.VisibilityClass == NoteVisibility.Restricted
                        ? "restricted-requires-grant" : "class-not-readable-by-role",
                    FieldClasses = [link.VisibilityClass.ToString().ToLowerInvariant()],
                    Severity = link.IsPhi ? AuditSeverity.High : AuditSeverity.Info,
                }, ct);
                return GateResults.Forbidden("urn:hbmp:document-download-denied",
                    detail: link.VisibilityClass == NoteVisibility.Restricted
                        ? "Restricted material is released only through a report-access grant."
                        : $"{link.VisibilityClass} documents are not downloadable by your role.",
                    reason: "download-not-permitted");
            }

            var url = await store.SignedDownloadUrlAsync(link.DocumentId, DownloadTtl, bearer, ct);
            if (url is null)
                return ProblemResults.Conflict("DOWNLOAD_UNAVAILABLE", "The document store could not mint a download URL.");

            // EVERY download is audited — who, what, when, and the stated purpose. This is the record that
            // answers "who looked at this member's discharge summary" a year later.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "policy_document", EntityId = linkId.ToString(), Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId, DecisionOutcome = "downloaded", DecisionReasonCode = purpose,
                FieldClasses = [link.VisibilityClass.ToString().ToLowerInvariant()],
                Severity = link.IsPhi ? AuditSeverity.High : AuditSeverity.Info,
            }, ct);

            return Results.Ok(new { linkId, url = url.ToString(), expiresInSeconds = (int)DownloadTtl.TotalSeconds });
        });
    }

    // ---- Withdraw / verify -------------------------------------------------------------------------------
    private static void MapLifecycle(RouteGroupBuilder write)
    {
        write.MapPost("/documents/{linkId:guid}/withdraw", async (Guid linkId, WithdrawDocument req,
            PolicyDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, IOutbox outbox,
            TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            if (string.IsNullOrWhiteSpace(req.Reason))
                return ProblemResults.Invalid("REASON_REQUIRED", "A reason is required to withdraw a document.");

            var link = await db.PolicyDocuments.FirstOrDefaultAsync(d => d.LinkId == linkId, ct);
            if (link is null) return NotFound();
            if (link.Status == DocumentLinkStatus.Withdrawn)
                return ProblemResults.Conflict("ALREADY_WITHDRAWN", "This document is already withdrawn.");

            // The row AND the bytes stay. "Wrong member" is a reason to mark a document, not to make the
            // mistake unfindable — and the withdrawal itself is often the thing a later review needs to see.
            var now = clock.GetUtcNow();
            link.Status = DocumentLinkStatus.Withdrawn;
            link.WithdrawnByUserId = SubjectId(principal);
            link.WithdrawnByUsername = principal.Subject;
            link.WithdrawnAt = now;
            link.WithdrawalReason = req.Reason.Trim();
            link.UpdatedAt = now;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "policy_document", EntityId = linkId.ToString(), Action = AuditAction.StateChange,
                ActorUserId = principal.Subject, TenantId = principal.TenantId,
                DecisionOutcome = "withdrawn", DecisionReasonCode = req.Reason,
            }, ct);
            await outbox.EnqueueAsync("DocumentWithdrawn", "policy.events",
                new { tenantId = link.TenantId, linkId, reason = req.Reason, by = principal.Subject }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(PolicyDocumentView.For(link, principal.Roles, BusinessCalendar.DateIn(now)));
        });

        write.MapPost("/documents/{linkId:guid}/verify", async (Guid linkId, VerifyDocument req,
            PolicyDbContext db, IHbmpPrincipalAccessor me, IAuditClient audit, IOutbox outbox,
            TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            var link = await db.PolicyDocuments.FirstOrDefaultAsync(d => d.LinkId == linkId, ct);
            if (link is null) return NotFound();
            if (link.Status == DocumentLinkStatus.Withdrawn)
                return ProblemResults.Conflict("WITHDRAWN", "A withdrawn document cannot be verified.");

            var now = clock.GetUtcNow();
            link.VerifiedByUserId = SubjectId(principal);
            link.VerifiedByUsername = principal.Subject;
            link.VerifiedAt = now;
            link.VerificationNote = req.Note;
            link.UpdatedAt = now;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "policy_document", EntityId = linkId.ToString(), Action = AuditAction.Update,
                ActorUserId = principal.Subject, TenantId = principal.TenantId, DecisionOutcome = "verified",
            }, ct);
            await outbox.EnqueueAsync("DocumentVerified", "policy.events",
                new { tenantId = link.TenantId, linkId, by = principal.Subject }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(PolicyDocumentView.For(link, principal.Roles, BusinessCalendar.DateIn(now)));
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>Bind the metadata from the multipart form. The signature is NOT bound — it comes from the
    /// token, like a note's.</summary>
    private static AttachDocumentRequest Bind(IFormCollection form) => new(
        form["documentClass"].FirstOrDefault() ?? "",
        form["title"].FirstOrDefault() ?? "",
        form["description"].FirstOrDefault(),
        DateOnly.TryParse(form["documentDate"].FirstOrDefault(), out var docDate) ? docDate : null,
        form["issuingProvider"].FirstOrDefault(),
        form["visibilityClass"].FirstOrDefault(),
        form["sensitiveCategory"].FirstOrDefault(),
        DateOnly.TryParse(form["expiresOn"].FirstOrDefault(), out var expiry) ? expiry : null,
        Guid.TryParse(form["supersedesLinkId"].FirstOrDefault(), out var supersedes) ? supersedes : null);

    private static Guid? SubjectId(HbmpPrincipal p) => Guid.TryParse(p.Subject, out var id) ? id : null;

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    private static async Task<IResult?> SaveOrConflict(PolicyDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg
                                           && pg.SqlState is "P0001" or "23514")
        {
            var pgEx = (Npgsql.PostgresException)ex.InnerException!;
            return pgEx.SqlState == "P0001"
                ? ProblemResults.Conflict("DOCUMENT_IMMUTABLE", pgEx.MessageText)
                : ProblemResults.Unprocessable("CHECK_VIOLATION", pgEx.MessageText);
        }
    }
}

/// <summary>A resolved identification photograph: which link and version, and a SHORT-TTL signed URL that is
/// dead long before it can be pasted anywhere useful (design 39 §5).</summary>
public sealed record IdentityPhotoView(Guid LinkId, int VersionNo, string SignedUrl, DateTimeOffset ExpiresAt);
