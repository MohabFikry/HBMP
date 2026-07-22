using System.Security.Cryptography;

namespace Mersal.Document.Domain;

/// <summary>Malware scanner port (ClamAV impl in Infrastructure). Fail-closed on a positive.</summary>
public interface IMalwareScanner
{
    Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default);
}

public sealed record ScanResult(bool IsClean, string? Signature)
{
    public static readonly ScanResult Clean = new(true, null);
    public static ScanResult Infected(string signature) => new(false, signature);
}

/// <summary>Blob store port (MinIO/S3 impl in Infrastructure). Returns the stored blob path.</summary>
public interface IBlobStore
{
    Task<string> PutAsync(string container, string key, Stream content, string contentType, CancellationToken ct = default);
}

/// <summary>Outcome of an upload attempt.</summary>
public abstract record UploadOutcome
{
    public sealed record Stored(Document Document, DocumentVersion Version) : UploadOutcome;
    public sealed record Rejected(string Reason) : UploadOutcome;
    public sealed record Quarantined(string Signature) : UploadOutcome;
}

/// <summary>
/// Validate → checksum → malware-scan → store → attach/version (US-002). Only clean files are
/// attached; a positive is quarantined + rejected (both audited by the caller). Blob bytes never
/// touch the RDBMS. This orchestration is pure over its ports so it is unit-tested with fakes.
/// </summary>
public sealed class DocumentUploadService(UploadValidator validator, IMalwareScanner scanner, IBlobStore blobs, TimeProvider clock)
{
    public async Task<UploadOutcome> UploadAsync(
        DocType docType, Guid ownerBeneficiaryId, Classification classification,
        string? contentType, byte[] content, string? uploadedBy, Document? existing = null,
        CancellationToken ct = default)
    {
        // 1. Validate BEFORE storing.
        var validation = validator.Validate(contentType, content?.LongLength ?? 0);
        if (!validation.IsValid) return new UploadOutcome.Rejected(validation.Reason!);

        // 2. Checksum.
        var checksum = Convert.ToHexString(SHA256.HashData(content!)).ToLowerInvariant();

        // 3. Malware scan — fail closed on a positive; nothing is stored.
        using (var scanStream = new MemoryStream(content!, writable: false))
        {
            var scan = await scanner.ScanAsync(scanStream, ct);
            if (!scan.IsClean) return new UploadOutcome.Quarantined(scan.Signature ?? "unknown");
        }

        // 4. Store the clean blob.
        var container = $"beneficiary-{ownerBeneficiaryId:N}";
        var versionNo = (existing?.CurrentVersionNo ?? 0) + 1;
        var key = $"{docType}/{Guid.NewGuid():N}-v{versionNo}";
        string blobPath;
        using (var putStream = new MemoryStream(content!, writable: false))
        {
            blobPath = await blobs.PutAsync(container, key, putStream, contentType!, ct);
        }

        // 5. Create/version the document with timestamp + uploader.
        var now = clock.GetUtcNow();
        var doc = existing ?? new Document
        {
            DocumentId = Guid.NewGuid(), DocType = docType, OwnerBeneficiaryId = ownerBeneficiaryId,
            Classification = classification, BlobContainer = container, CreatedAt = now,
        };
        doc.CurrentVersionNo = versionNo;
        var version = new DocumentVersion
        {
            DocumentVersionId = Guid.NewGuid(), DocumentId = doc.DocumentId, VersionNo = versionNo,
            BlobPath = blobPath, ChecksumSha256 = checksum, SizeBytes = content!.LongLength,
            UploadedAt = now, UploadedBy = uploadedBy,
        };
        doc.Versions.Add(version);
        return new UploadOutcome.Stored(doc, version);
    }
}
