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
        _opt = options.Value;
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
