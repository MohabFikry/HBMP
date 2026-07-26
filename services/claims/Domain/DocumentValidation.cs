namespace Mersal.Claims.Domain;

/// <summary>Pure validation for a document attachment (10b.5, §3.2 "type + size validation"). The bytes live in
/// document-service (scanned + encrypted at rest); claims-service stores only a reference, so all it validates is the
/// declared type, size, and doc-type classification before recording the link. Returns an error token or null.</summary>
public static class DocumentValidation
{
    /// <summary>25 MB — an invoice/receipt/statement scan; larger uploads are rejected at the seam.</summary>
    public const long MaxSizeBytes = 25L * 1024 * 1024;

    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png", "image/tiff", "image/heic",
    };

    /// <summary>Returns null when valid, else a coded error token for a 422/415 problem response.</summary>
    public static string? Validate(string? contentType, long sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedContentTypes.Contains(contentType))
            return "unsupported-content-type";
        if (sizeBytes <= 0) return "empty-document";
        if (sizeBytes > MaxSizeBytes) return "document-too-large";
        return null;
    }
}
