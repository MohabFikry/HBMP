using Amazon.S3;
using Amazon.S3.Model;
using Mersal.Document.Domain;
using Microsoft.Extensions.Options;

namespace Mersal.Document.Infrastructure;

public sealed class BlobStoreOptions
{
    public const string SectionName = "Blob";
    public string Endpoint { get; set; } = "http://minio:9000";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = "beneficiary-documents";
}

/// <summary>
/// Stores document blobs in MinIO/S3 (private bucket). Only clean, validated files reach here.
/// The RDBMS holds metadata only; bytes live here (15-database-erd §12).
/// </summary>
public sealed class MinioBlobStore : IBlobStore, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly BlobStoreOptions _opt;
    private bool _bucketEnsured;

    public MinioBlobStore(IOptions<BlobStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _opt = options.Value;
        // 18.B1 (audit R2 X4): the access key and secret for the PHI blob bucket were committed in the
        // BASE appsettings.json — the .env.example default, in a tracked file — so any run outside compose
        // reached every beneficiary document with a published credential. They are configuration-only now,
        // and a missing one fails at STARTUP rather than silently falling back to a known value.
        if (string.IsNullOrWhiteSpace(_opt.AccessKey) || string.IsNullOrWhiteSpace(_opt.SecretKey))
            throw new InvalidOperationException(
                "Blob storage credentials are not configured — inject Blob__AccessKey / Blob__SecretKey via " +
                "environment or OpenBao. They are never baked into appsettings (the bucket holds PHI).");
        _s3 = new AmazonS3Client(_opt.AccessKey, _opt.SecretKey, new AmazonS3Config
        {
            ServiceURL = _opt.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });
    }

    public async Task<string> PutAsync(string container, string key, Stream content, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var objectKey = $"{container}/{key}";
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opt.Bucket,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, ct);
        return $"s3://{_opt.Bucket}/{objectKey}";
    }

    public async Task<Stream?> GetAsync(string blobPath, CancellationToken ct = default)
    {
        // PutAsync returns "s3://{bucket}/{container}/{key}"; read the same shape back rather than storing a
        // second copy of the location.
        if (string.IsNullOrWhiteSpace(blobPath) || !blobPath.StartsWith("s3://", StringComparison.Ordinal)) return null;
        var withoutScheme = blobPath["s3://".Length..];
        var slash = withoutScheme.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return null;
        var bucket = withoutScheme[..slash];
        var key = withoutScheme[(slash + 1)..];

        try
        {
            var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception)
        {
            return null;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketEnsured) return;
        if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_s3, _opt.Bucket))
        {
            await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _opt.Bucket }, ct);
        }
        _bucketEnsured = true;
    }

    public void Dispose() => _s3.Dispose();
}
