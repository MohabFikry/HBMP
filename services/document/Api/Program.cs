using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Document.Domain;
using Mersal.Document.Infrastructure;
using Mersal.Events;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("document-service");
builder.Services.AddHbmpAuthorization();
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true);
builder.Services.AddDocumentInfrastructure(builder.Configuration);
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("document-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter());
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "document-service" })).AllowAnonymous();

var v1 = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("document:write"));

// Upload a document for a beneficiary (US-002): validate type/size BEFORE storing → checksum →
// malware scan (fail-closed) → store clean blob → create/version metadata. Every path is audited.
v1.MapPost("/beneficiaries/{beneficiaryId:guid}/documents", async (
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
            db.Documents.Add(s.Document);
            await db.SaveChangesAsync(ct);
            await audit.EmitAsync(Draft(beneficiaryId, AuditAction.Create, actor, "stored", s.Version.ChecksumSha256, AuditSeverity.Info, s.Document.DocumentId), ct);
            await outbox.EnqueueAsync("DocumentAttached", "document.events",
                new { documentId = s.Document.DocumentId, beneficiaryId, docType = type.ToString(), version = s.Version.VersionNo }, ct);
            return Results.Created($"/api/v1/beneficiaries/{beneficiaryId}/documents/{s.Document.DocumentId}",
                new { s.Document.DocumentId, docType = type.ToString(), version = s.Version.VersionNo, s.Version.ChecksumSha256, s.Version.SizeBytes });

        default:
            return Results.Problem(statusCode: 500, title: "unexpected");
    }
})
.DisableAntiforgery();

// List a beneficiary's document metadata (min-necessary; no blob bytes).
v1.MapGet("/beneficiaries/{beneficiaryId:guid}/documents", async (Guid beneficiaryId, DocumentDbContext db, CancellationToken ct) =>
{
    var docs = await db.Documents.AsNoTracking().Include(d => d.Versions)
        .Where(d => d.OwnerBeneficiaryId == beneficiaryId && !d.IsDeleted)
        .ToListAsync(ct);
    return Results.Ok(docs.Select(d => new
    {
        d.DocumentId, docType = d.DocType.ToString(), classification = d.Classification.ToString(),
        d.CurrentVersionNo, versions = d.Versions.Select(v => new { v.VersionNo, v.ChecksumSha256, v.SizeBytes, v.UploadedAt, v.UploadedBy }),
    }));
}).RequireAuthorization();

app.Run();

static AuditEventDraft Draft(Guid beneficiaryId, AuditAction action, string? actor, string outcome, string? detail,
    AuditSeverity severity = AuditSeverity.Info, Guid? docId = null) => new()
{
    EntityType = "document", EntityId = (docId ?? beneficiaryId).ToString(),
    Action = action, ActorUserId = actor, Severity = severity,
    DecisionOutcome = outcome, DecisionReasonCode = detail, FieldClasses = ["phi"],
};

public partial class Program;
