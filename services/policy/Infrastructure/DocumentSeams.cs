using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mersal.Policy.Infrastructure;

/// <summary>The outcome of handing bytes to document-service. Quarantined is a distinct case, not an error:
/// the scan working correctly on an infected file is the system doing its job, and the caller needs to be told
/// that rather than "upload failed".</summary>
public abstract record DocumentStoreResult
{
    public sealed record Stored(Guid DocumentId, int VersionNo, string ChecksumSha256) : DocumentStoreResult;
    public sealed record Rejected(string Reason) : DocumentStoreResult;
    public sealed record Quarantined(string Signature) : DocumentStoreResult;
}

/// <summary>
/// Phase 19.3b — the seam onto document-service, which owns the whole upload pipeline: MIME and size
/// validation, the FAIL-CLOSED ClamAV scan, checksum_sha256, MinIO storage and blob versioning.
///
/// <para>policy-service adds linkage and classification and nothing else. A second upload path here would be a
/// second place for malware to get in and a second place for retention to be forgotten — which is exactly why
/// the build prompt says reuse, do not rebuild.</para>
/// </summary>
public interface IDocumentStore
{
    Task<DocumentStoreResult> StoreAsync(
        Guid beneficiaryId, string docType, string contentType, byte[] bytes, string? bearerToken,
        CancellationToken ct = default);

    /// <summary>A SHORT-TTL signed URL, minted per request. Never a permanent link: a durable URL is a
    /// credential that leaks into browser history, chat logs and support tickets, and outlives every
    /// authorization decision that produced it.</summary>
    Task<Uri?> SignedDownloadUrlAsync(Guid documentId, TimeSpan ttl, string? bearerToken, CancellationToken ct = default);
}

public sealed class HttpDocumentStore(HttpClient http) : IDocumentStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DocumentStoreResult> StoreAsync(
        Guid beneficiaryId, string docType, string contentType, byte[] bytes, string? bearerToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", "upload");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/beneficiaries/{beneficiaryId}/documents?docType={Uri.EscapeDataString(docType)}")
        { Content = content };
        Authorize(req, bearerToken);

        using var resp = await http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
        {
            var dto = await resp.Content.ReadFromJsonAsync<StoredDto>(Json, ct);
            return dto is null
                ? new DocumentStoreResult.Rejected("document-service returned no document reference")
                : new DocumentStoreResult.Stored(dto.DocumentId, dto.Version, dto.ChecksumSha256);
        }

        var problem = await resp.Content.ReadAsStringAsync(ct);
        // 422 is document-service's malware verdict. It must surface as quarantine, not as a generic failure,
        // or an operator retries an infected file believing the upload glitched.
        return (int)resp.StatusCode == 422
            ? new DocumentStoreResult.Quarantined(problem)
            : new DocumentStoreResult.Rejected(problem);
    }

    public async Task<Uri?> SignedDownloadUrlAsync(
        Guid documentId, TimeSpan ttl, string? bearerToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/documents/{documentId}/signed-url?ttlSeconds={(int)ttl.TotalSeconds}");
        Authorize(req, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var dto = await resp.Content.ReadFromJsonAsync<SignedUrlDto>(Json, ct);
        return dto is null ? null : new Uri(dto.Url);
    }

    private static void Authorize(HttpRequestMessage req, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return;
        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..] : bearerToken;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record StoredDto(Guid DocumentId, int Version, string ChecksumSha256);
    private sealed record SignedUrlDto(string Url);
}

/// <summary>
/// Phase 19.3b — the OCR seam (design 13 <c>IDocumentOcrProvider</c>).
///
/// WIRED, NOT IMPLEMENTED, exactly as the build prompt specifies. Extraction from a scanned past medical
/// history is genuinely useful and is also the kind of feature that quietly becomes authoritative: an OCR'd
/// diagnosis rendered beside a real one is indistinguishable to a reader. When it lands it must be assistive
/// and human-gated, like the claims OCR in 10b.6.
/// </summary>
public interface IPolicyDocumentOcr
{
    Task<string?> ExtractTextAsync(Guid documentId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Default: no extraction. Returns null rather than an empty string, so a caller cannot mistake
/// "OCR is not enabled" for "the document contained no text".</summary>
public sealed class DisabledPolicyDocumentOcr : IPolicyDocumentOcr
{
    public Task<string?> ExtractTextAsync(Guid documentId, string? bearerToken, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
