using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Data;
using Mersal.Document.Api;
using Mersal.Document.Domain;
using Mersal.Document.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("document-service");
builder.Services.AddHbmpAuthorization(DocumentPolicies.Bundle());
builder.Services.AddHbmpBreakGlass(builder.Configuration); // live break-glass elevation (16.6, H5)
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<DocumentDbContext>();
// The relay belongs with the outbox. Without it this service staged every event it ever raised — including
// the audit events AddHbmpEvents routes through the outbox — into document.outbox and delivered none of
// them, so uploads and downloads of identified-person extracts left no trail beyond this box.
builder.Services.AddHbmpOutboxRelay();
builder.Services.AddDocumentInfrastructure(builder.Configuration);
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("document-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Readiness for the probe in infra/helm/rollout/rollout-template.yaml. Process-level only: this reports
// "through startup and able to serve". A dependency check here would pull the pod out of rotation for a
// condition the service already surfaces per-request, turning a partial degradation into a total outage.
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
// 21.4 — the THIRD gate, asked LAST: after authorization, before execution (design 40 §4). Placed here
// rather than on each route group because document storage is one programme, and a per-group
// filter is one chance per group to forget the next one. Health probes are anonymous and the event
// pipeline is tenant-less, so neither needs an exemption.
app.UseProgramFeature(ProgramFeatures.Documents);
app.UseHbmpRls(); // bind app.tenant_id GUC from the principal (RLS, ADR-0011)
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "document-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

// Writes require the write scope; reads no longer ride on the write scope (H9) — the read group is
// authenticated + row/role-authorized per-request through the engine (see the GET below).
var writes = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("document:write"));
var reads = app.MapGroup("/api/v1").RequireAuthorization();

// Upload a document for a beneficiary (US-002): validate type/size BEFORE storing → checksum →
// malware scan (fail-closed) → store clean blob → create/version metadata. Every path is audited.
writes.MapPost("/beneficiaries/{beneficiaryId:guid}/documents", async (
    Guid beneficiaryId, string docType, string? classification, IFormFile file,
    DocumentUploadService uploads, DocumentDbContext db, IAuditClient audit, IOutbox outbox,
    IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    if (file is null || file.Length == 0) return Results.Problem(statusCode: 400, title: "no file");
    if (!Enum.TryParse<DocType>(docType, ignoreCase: true, out var type))
        return Results.Problem(statusCode: 400, title: $"invalid docType '{docType}'");
    var cls = Enum.TryParse<Classification>(classification, ignoreCase: true, out var c) ? c : Classification.PHI;

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var bytes = ms.ToArray();
    var actor = me.Principal?.Subject;

    var outcome = await uploads.UploadAsync(type, beneficiaryId, cls, file.ContentType, bytes, actor, existing: null, ct);

    switch (outcome)
    {
        case UploadOutcome.Rejected r:
            await audit.EmitAsync(Draft(beneficiaryId, AuditAction.Create, actor, "rejected", r.Reason), ct);
            return Results.Problem(statusCode: 400, title: "upload-rejected", detail: r.Reason, type: "urn:hbmp:upload-rejected");

        case UploadOutcome.Quarantined q:
            await audit.EmitAsync(Draft(beneficiaryId, AuditAction.Create, actor, "quarantined", q.Signature, AuditSeverity.High), ct);
            return Results.Problem(statusCode: 422, title: "malware-detected", detail: $"quarantined: {q.Signature}", type: "urn:hbmp:malware-quarantined");

        case UploadOutcome.Stored s:
        {
            // 24.3 — a stored document whose DocumentAttached event is lost is a file in MinIO that no
            // record anywhere points at.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Documents.Add(s.Document);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(beneficiaryId, AuditAction.Create, actor, "stored", s.Version.ChecksumSha256, AuditSeverity.Info, s.Document.DocumentId), ct);
            await outbox.EnqueueAsync("DocumentAttached", "document.events",
                new { documentId = s.Document.DocumentId, beneficiaryId, docType = type.ToString(), version = s.Version.VersionNo }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/beneficiaries/{beneficiaryId}/documents/{s.Document.DocumentId}",
                new { s.Document.DocumentId, docType = type.ToString(), version = s.Version.VersionNo, s.Version.ChecksumSha256, s.Version.SizeBytes });
        }

        default:
            return Results.Problem(statusCode: 500, title: "unexpected");
    }
})
.DisableAntiforgery();

// List a beneficiary's document metadata (min-necessary; no blob bytes). H9: row/role-authorized via the
// engine (tenant + reader role; document read is Sensitive so the engine audits the PHI access) — previously
// any document:write holder could list ANY beneficiary's documents with no authorization and no audit trail.
reads.MapGet("/beneficiaries/{beneficiaryId:guid}/documents", async (
    Guid beneficiaryId, DocumentDbContext db, IAuthorizationEngine engine, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var p = me.Principal;
    if (p is null) return GateResults.Unauthenticated();
    var resource = new ResourceRef
    {
        Type = DocumentPolicies.Resource, Id = beneficiaryId.ToString(),
        TenantId = p.TenantId, BeneficiaryId = beneficiaryId.ToString(),
    };
    var decision = await engine.EvaluateAsync(new AuthzRequest(p, DocumentPolicies.Read, resource, "document-read"), ct);
    if (!decision.IsAllowed) // engine already audited the attempted PHI access
        return GateResults.Forbidden("urn:hbmp:document-access-denied",
            detail: "You are not permitted to read this beneficiary's documents.", reason: decision.ReasonCode);

    var docs = await db.Documents.AsNoTracking().Include(d => d.Versions)
        .Where(d => d.OwnerBeneficiaryId == beneficiaryId && !d.IsDeleted)
        .ToListAsync(ct);
    return Results.Ok(docs.Select(d => new
    {
        d.DocumentId, docType = d.DocType.ToString(), classification = d.Classification.ToString(),
        d.CurrentVersionNo, versions = d.Versions.Select(v => new { v.VersionNo, v.ChecksumSha256, v.SizeBytes, v.UploadedAt, v.UploadedBy }),
    }));
});

/*
 * 33.8 — THE BYTES OF A CLINICAL DOCUMENT.
 *
 * Until now this service could store a beneficiary's clinical document and list its metadata, and nothing
 * anywhere could read it back: `/operational-documents/{id}/content` serves a DIFFERENT table (bulk uploads,
 * error reports, extracts), so a clinical document id 404s there. A radiographer had been able to upload a
 * signed report or a DICOM study since phase 5.3 — scanned, encrypted, checksummed, its id pinned on the
 * fulfillment row — and no role in the platform could retrieve it. For radiology, where the report IS the
 * result, the product stored the finding and showed the ordering doctor a one-line summary of it.
 *
 * GATED ON ContentRead, NOT Read. Read is metadata and says so in as many words ("never blob bytes"): seeing
 * that a document is on file and reading it are different disclosures with different role lists.
 *
 * AND THIS IS THE SECOND GATE, NOT THE FIRST, for anything attached to an investigation line. The 14.7
 * sensitivity rule turns on the line's sensitivity and the caller's time-boxed grants — facts this service
 * does not hold — so it lives in orders-service, and the clinician path is
 * `GET /investigation-orders/{orderId}/lines/{lineId}/result/report`, which applies it and then calls here
 * with the caller's own bearer so both checks run. The document id required to reach this endpoint is only
 * ever handed out by that gate: the restricted projection returns "never values, never a document ref".
 */
reads.MapGet("/beneficiaries/{beneficiaryId:guid}/documents/{documentId:guid}/content", async (
    Guid beneficiaryId, Guid documentId, DocumentDbContext db, IBlobStore blobs,
    IAuthorizationEngine engine, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var p = me.Principal;
    if (p is null) return GateResults.Unauthenticated();

    var resource = new ResourceRef
    {
        Type = DocumentPolicies.Resource, Id = documentId.ToString(),
        TenantId = p.TenantId, BeneficiaryId = beneficiaryId.ToString(),
    };
    var decision = await engine.EvaluateAsync(new AuthzRequest(p, DocumentPolicies.ContentRead, resource, "document-content-read"), ct);
    if (!decision.IsAllowed) // engine already audited the attempted PHI access
        return GateResults.Forbidden("urn:hbmp:document-access-denied",
            detail: "You are not permitted to read this document's content.", reason: decision.ReasonCode);

    var doc = await db.Documents.AsNoTracking().Include(d => d.Versions)
        .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.OwnerBeneficiaryId == beneficiaryId && !d.IsDeleted, ct);
    // Matched on BOTH ids: a document id alone must not be a key into another beneficiary's file, and RLS
    // already bounds this to the tenant.
    if (doc is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    var version = doc.Versions.OrderByDescending(v => v.VersionNo).FirstOrDefault();
    if (version is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    var stream = await blobs.GetAsync(version.BlobPath, ct);
    if (stream is null)
        return Results.Problem(statusCode: 502, title: "blob-unavailable", type: "urn:hbmp:blob-unavailable",
            detail: "The stored file could not be read from object storage.");

    // EVERY read is an Export, on the same reasoning the operational download states: the fourth retrieval of
    // a report discloses exactly as much as the first.
    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = "document", EntityId = documentId.ToString(), Action = AuditAction.Export,
        ActorUserId = p.Subject, ActorRole = string.Join(',', p.Roles), TenantId = p.TenantId,
        DecisionOutcome = "downloaded", DecisionReasonCode = doc.DocType.ToString(),
        FieldClasses = ["phi"], Severity = AuditSeverity.High,
    }, ct);

    /*
     * `application/octet-stream`, and a name built from the doc type rather than the one the uploader used.
     *
     * The clinical path persists NEITHER: `DocumentVersion` carries blob path, checksum, size and who
     * uploaded it, and no media type or original filename — unlike `OperationalDocument`, which stores both.
     * The media type does reach MinIO on the object, but `IBlobStore.GetAsync` returns a bare stream.
     *
     * Left as a stated limitation rather than guessed at: sniffing a type from the first bytes would be a
     * guess presented as a fact, and adding two columns is a migration this fix does not need. A download is
     * a download; a caller that must know the format has the DocType.
     */
    return Results.Stream(stream, "application/octet-stream", $"{doc.DocType}-{documentId.ToString()[..8]}");
})
.Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");

app.MapOperationalDocuments();   // 19.5b — bulk uploads, error reports and extracts; same scan, same store

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

app.Run();

static AuditEventDraft Draft(Guid beneficiaryId, AuditAction action, string? actor, string outcome, string? detail,
    AuditSeverity severity = AuditSeverity.Info, Guid? docId = null) => new()
{
    EntityType = "document", EntityId = (docId ?? beneficiaryId).ToString(),
    Action = action, ActorUserId = actor, Severity = severity,
    DecisionOutcome = outcome, DecisionReasonCode = detail, FieldClasses = ["phi"],
};

public partial class Program;
