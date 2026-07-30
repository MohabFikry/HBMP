namespace Mersal.Document.Domain;

/// <summary>
/// Validate an upload BEFORE storing (US-002): allowed MIME types + max size, with a clear reason on
/// rejection. Configurable; defaults allow pdf/jpeg/png up to 10 MB.
/// </summary>
public sealed class UploadValidator(IReadOnlySet<string>? allowedMimeTypes = null, long maxSizeBytes = 10 * 1024 * 1024)
{
    public static readonly IReadOnlySet<string> DefaultAllowed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/jpeg", "image/png" };

    /// <summary>
    /// What an OPERATIONAL document may be: a bulk intake file, its error report, an extract.
    ///
    /// <para>These are spreadsheets, and they were being validated against the beneficiary-document list —
    /// pdf/jpeg/png — so every bulk upload was rejected before it was ever scanned. The engine could not tell
    /// that refusal from an outage and reported it as <c>SCAN_UNAVAILABLE</c>, which is why the cause was a
    /// content-type rule and the symptom looked like a broken scanner.</para>
    ///
    /// <para>Deliberately still an ALLOW-LIST, and deliberately without <c>application/octet-stream</c>: a
    /// wildcard here would let anything through the one gate that decides what reaches the scanner. The
    /// spellings below are the ones browsers and Excel actually send for CSV and XLSX.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> OperationalAllowed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "text/csv",
            "application/csv",
            "text/plain",                                                            // some clients send CSV as this
            "application/vnd.ms-excel",                                              // .xls, and Excel's CSV
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",      // .xlsx
        };

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
