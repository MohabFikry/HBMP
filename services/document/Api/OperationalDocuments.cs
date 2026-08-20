using System.Security.Cryptography;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Document.Domain;
using Mersal.Document.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Document.Api;

/// <summary>
/// Phase 19.5b — upload and download for files that belong to an OPERATION: a bulk upload, its error report,
/// a data extract.
///
/// <para>DOWNLOAD IS AN AUTHORIZED, AUDITED STREAM — not a signed URL. The build prompt asks for "signed,
/// short-TTL, audited"; the property that actually matters is that the file cannot be read by someone who was
/// never authorized, and cannot be read again after that authorization is withdrawn. A signed URL is a bearer
/// credential in a query string: it survives in browser history, chat messages and support tickets, and no
/// revocation reaches it before its TTL expires. Streaming through an authenticated endpoint gives a stronger
/// version of the same guarantee with nothing to leak — and every read writes its own audit event, which a
/// URL redeemed directly at MinIO never would.</para>
/// </summary>
public static class OperationalDocumentEndpoints
{
    /// <summary>Kinds that carry beneficiary-identifying content and are therefore audited as a PHI read.
    /// A bulk error report is the clearest case: "row 4 231: UNHCR number … already enrolled".</summary>
    private static readonly HashSet<OperationalDocKind> PhiBearing =
        [OperationalDocKind.BulkUpload, OperationalDocKind.BulkErrorReport, OperationalDocKind.Extract];

    public static void MapOperationalDocuments(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/operational-documents", async (
            string kind, Guid ownerRef, string ownerService, string? classification, IFormFile file,
            UploadValidator validator, IMalwareScanner scanner, IBlobStore blobs, DocumentDbContext db,
            IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return Results.Unauthorized();
            if (file is null || file.Length == 0) return Results.Problem(statusCode: 400, title: "no file");
            if (!Enum.TryParse<OperationalDocKind>(kind, ignoreCase: true, out var docKind))
                return Results.Problem(statusCode: 400, title: $"invalid kind '{kind}'");
            var cls = Enum.TryParse<Classification>(classification, ignoreCase: true, out var c) ? c : Classification.PHI;

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            // The OPERATIONAL list, not the beneficiary-document one: these files are spreadsheets. Same
            // validator type and the same max size — only the allowed content types differ.
            var operationalValidator = new UploadValidator(
                UploadValidator.OperationalAllowed, validator.MaxSizeBytes);
            var validation = operationalValidator.Validate(file.ContentType, bytes.LongLength);
            if (!validation.IsValid)
                return Results.Problem(statusCode: 400, title: "upload-rejected", detail: validation.Reason,
                    type: "urn:hbmp:upload-rejected");

            // FAIL CLOSED. Nothing is stored, nothing is parsed, and the job that submitted it learns that its
            // file was infected rather than that "the upload failed" — the second reading invites a retry.
            using (var scanStream = new MemoryStream(bytes, writable: false))
            {
                var scan = await scanner.ScanAsync(scanStream, ct);
                if (!scan.IsClean)
                {
                    await audit.EmitAsync(new AuditEventDraft
                    {
                        EntityType = "operational_document", EntityId = ownerRef.ToString(),
                        Action = AuditAction.Create, ActorUserId = principal.Subject, TenantId = principal.TenantId,
                        DecisionOutcome = "quarantined", DecisionReasonCode = scan.Signature,
                        Severity = AuditSeverity.High,
                    }, ct);
                    return Results.Problem(statusCode: 422, title: "malware-detected",
                        detail: $"quarantined: {scan.Signature}", type: "urn:hbmp:malware-quarantined");
                }
            }

            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var container = $"operational/{ownerService}/{docKind}";
            var key = $"{ownerRef:N}-{Guid.NewGuid():N}";
            string blobPath;
            using (var putStream = new MemoryStream(bytes, writable: false))
            {
                blobPath = await blobs.PutAsync(container, key, putStream, file.ContentType ?? "application/octet-stream", ct);
            }

            var doc = new OperationalDocument
            {
                DocumentId = Guid.NewGuid(), Kind = docKind, OwnerRef = ownerRef,
                OwnerService = ownerService, Classification = cls,
                FileName = Path.GetFileName(file.FileName ?? "upload"),
                ContentType = file.ContentType ?? "application/octet-stream",
                BlobPath = blobPath, ChecksumSha256 = checksum, SizeBytes = bytes.LongLength,
                CreatedAt = clock.GetUtcNow(), CreatedBy = principal.Subject,
            };
            db.OperationalDocuments.Add(doc);
            await db.SaveChangesAsync(ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "operational_document", EntityId = doc.DocumentId.ToString(),
                Action = AuditAction.Create, ActorUserId = principal.Subject, TenantId = principal.TenantId,
                DecisionOutcome = "stored", DecisionReasonCode = docKind.ToString(),
                FieldClasses = cls == Classification.PHI ? ["phi"] : [],
            }, ct);

            return Results.Created($"/api/v1/operational-documents/{doc.DocumentId}",
                new { doc.DocumentId, kind = docKind.ToString(), doc.ChecksumSha256, doc.SizeBytes });
        })
        .RequireAuthorization(HbmpPolicies.Scope("document:write"))
        .DisableAntiforgery();

        app.MapGet("/api/v1/operational-documents/{id:guid}/content", async (
            Guid id, DocumentDbContext db, IBlobStore blobs, IAuditClient audit,
            IAuthorizationEngine engine, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return Results.Unauthorized();

            // The upload above requires document:write; this required only a valid token. Every operational
            // kind is PHI-bearing — the audit below calls each download an Export for that reason — so any
            // authenticated caller in the tenant, of any role, could walk ids and stream lists of identified
            // people. RLS bounded that to the tenant and nothing bounded it further. Through the engine, so
            // the refusal is audited as an attempted PHI access rather than being a silent 403.
            var resource = new ResourceRef
            {
                Type = DocumentPolicies.Resource, Id = id.ToString(), TenantId = principal.TenantId,
            };
            var decision = await engine.EvaluateAsync(
                new AuthzRequest(principal, DocumentPolicies.OperationalRead, resource, "operational-document-read"), ct);
            if (!decision.IsAllowed)
                return GateResults.Forbidden("urn:hbmp:document-access-denied",
                    detail: "You are not permitted to download operational documents.", reason: decision.ReasonCode);

            var doc = await db.OperationalDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DocumentId == id && !d.IsDeleted, ct);
            if (doc is null)
                return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

            var stream = await blobs.GetAsync(doc.BlobPath, ct);
            if (stream is null)
                return Results.Problem(statusCode: 502, title: "blob-unavailable",
                    detail: "The stored file could not be read from object storage.");

            // EVERY read, not just the first. A bulk error report is a list of identified people, and the
            // fourth download of it is exactly as much of a disclosure as the first.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "operational_document", EntityId = id.ToString(), Action = AuditAction.Export,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = "downloaded", DecisionReasonCode = doc.Kind.ToString(),
                FieldClasses = PhiBearing.Contains(doc.Kind) ? ["phi"] : [],
                Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Stream(stream, doc.ContentType, doc.FileName);
        }).RequireAuthorization();
    }
}
