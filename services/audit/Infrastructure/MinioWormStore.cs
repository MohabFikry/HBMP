using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Mersal.Audit.Client;
using Mersal.Audit.Domain;
using Microsoft.Extensions.Options;

namespace Mersal.Audit.Infrastructure;

public sealed class WormStoreOptions
{
    public const string SectionName = "Worm";
    public string Endpoint { get; set; } = "http://minio:9000";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = "hbmp-audit-worm";
    /// <summary>Object-lock retention (days). Governance/compliance retention window.</summary>
    public int RetentionDays { get; set; } = 3650;
}

/// <summary>
/// Writes a tamper-evident WORM copy of each chained record to MinIO with object-lock
/// (COMPLIANCE mode + retain-until), the independent second store beyond PostgreSQL
/// (19-audit-strategy.md §4). Objects cannot be overwritten or deleted before retention expiry.
/// </summary>
public sealed class MinioWormStore : IWormStore, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly WormStoreOptions _opt;

    public MinioWormStore(IOptions<WormStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _opt = options.Value;
        // 18.B1: the WORM bucket holds the immutable audit trail — the evidence that everything else is
        // trustworthy. Its credentials were committed in the base appsettings.json (the same MinIO key as
        // the PHI document bucket), so anyone with the repo could read or tamper with it. Configuration
        // only now, and a missing credential fails at STARTUP.
        if (string.IsNullOrWhiteSpace(_opt.AccessKey) || string.IsNullOrWhiteSpace(_opt.SecretKey))
            throw new InvalidOperationException(
                "WORM store credentials are not configured — inject Worm__AccessKey / Worm__SecretKey via " +
                "environment or OpenBao. They are never baked into appsettings (this bucket is the audit trail).");
        var config = new AmazonS3Config
        {
            ServiceURL = _opt.Endpoint,
            ForcePathStyle = true, // MinIO
            AuthenticationRegion = "us-east-1",
        };
        _s3 = new AmazonS3Client(_opt.AccessKey, _opt.SecretKey, config);
    }

    public async Task PersistAsync(AuditEvent chained, CancellationToken ct = default)
    {
        var key = $"{AuditPartition.KeyFor(chained.OccurredAt)}/{chained.AuditEventId:N}.json";
        var body = JsonSerializer.Serialize(chained);

        var request = new PutObjectRequest
        {
            BucketName = _opt.Bucket,
            Key = key,
            ContentBody = body,
            ContentType = "application/json",
            ObjectLockMode = ObjectLockMode.Compliance,
            ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(_opt.RetentionDays),
        };
        // Integrity: let S3 verify the payload checksum on write.
        request.ChecksumAlgorithm = ChecksumAlgorithm.SHA256;
        request.Metadata.Add("record-hash", chained.RecordHash ?? string.Empty);

        await _s3.PutObjectAsync(request, ct);
    }

    public void Dispose() => _s3.Dispose();

    internal static string Serialize(AuditEvent e) => JsonSerializer.Serialize(e);
    internal static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
