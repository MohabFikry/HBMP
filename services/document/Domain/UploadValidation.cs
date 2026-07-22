namespace Mersal.Document.Domain;

/// <summary>
/// Validate an upload BEFORE storing (US-002): allowed MIME types + max size, with a clear reason on
/// rejection. Configurable; defaults allow pdf/jpeg/png up to 10 MB.
/// </summary>
public sealed class UploadValidator(IReadOnlySet<string>? allowedMimeTypes = null, long maxSizeBytes = 10 * 1024 * 1024)
{
    public static readonly IReadOnlySet<string> DefaultAllowed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/jpeg", "image/png" };

    private readonly IReadOnlySet<string> _allowed = allowedMimeTypes ?? DefaultAllowed;

    public long MaxSizeBytes { get; } = maxSizeBytes;

    public UploadValidationResult Validate(string? contentType, long sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(contentType) || !_allowed.Contains(contentType))
            return UploadValidationResult.Rejected($"content type '{contentType}' is not allowed (allowed: {string.Join(", ", _allowed)})");
        if (sizeBytes <= 0)
            return UploadValidationResult.Rejected("empty file");
        if (sizeBytes > MaxSizeBytes)
            return UploadValidationResult.Rejected($"file size {sizeBytes} exceeds max {MaxSizeBytes} bytes");
        return UploadValidationResult.Ok;
    }
}

public sealed record UploadValidationResult(bool IsValid, string? Reason)
{
    public static readonly UploadValidationResult Ok = new(true, null);
    public static UploadValidationResult Rejected(string reason) => new(false, reason);
}
