using Mersal.Claims.Domain;

namespace Mersal.Claims.Infrastructure;

/// <summary>Pluggable OCR extraction (36 §3.3, phase-13 <c>IDocumentOcrProvider</c>). Extracts candidate fields — each
/// with a confidence score and source region — from a document held in document-service. The DEFAULT is a self-hosted
/// Arabic+English engine (Tesseract <c>ara+eng</c>) running in-cluster: no external SaaS, no PHI leaving the deployment.
/// Registered by DI and covered by a swappability test — a second implementation is used with no code change. OCR is
/// ASSISTIVE, never authoritative: nothing here makes a value payable; a human confirms first.</summary>
public interface IDocumentOcrProvider
{
    string Engine { get; }
    string EngineVersion { get; }
    Task<IReadOnlyList<OcrField>> ExtractAsync(
        Guid documentId, string languages, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Default OCR provider seam: extracts NOTHING, so every request routes to ManualAssessment until the
/// in-cluster Tesseract engine is wired (documents live in document-service). Never fabricates a field — a false
/// extraction would mislead the reviewing officer.</summary>
public sealed class NullOcrProvider : IDocumentOcrProvider
{
    public string Engine => "none";
    public string EngineVersion => "0";
    public Task<IReadOnlyList<OcrField>> ExtractAsync(
        Guid documentId, string languages, string? bearerToken, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OcrField>>([]);
}

/// <summary>Result of a malware scan of an uploaded document.</summary>
public sealed record ScanResult(bool Clean, string? Signature = null);

/// <summary>Malware scan of an uploaded document before it is accepted (36 §3.3, ClamAV per 0C). The bytes are scanned
/// in/around document-service; claims-service gates on the verdict and audits any rejection. The DEFAULT trusts an
/// upstream scan (returns clean) — the real ClamAV wiring lands with document-service integration.</summary>
public interface IDocumentScanner
{
    Task<ScanResult> ScanAsync(Guid documentId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Default scanner: assumes the upstream document-service scan passed. Replaceable by a ClamAV-backed scanner.</summary>
public sealed class CleanDocumentScanner : IDocumentScanner
{
    public Task<ScanResult> ScanAsync(Guid documentId, string? bearerToken, CancellationToken ct = default) =>
        Task.FromResult(new ScanResult(true));
}

/// <summary>An authorized underlying service (order/prescription) that a reimbursement can be matched against, and the
/// contract facts needed to price it. Returned by <see cref="IAuthorizedServiceResolver"/>. A reimbursement CANNOT
/// auto-match without one (a hard prerequisite, 36 §3.3).</summary>
public sealed record AuthorizedService(
    Guid? OrderId, Guid? PrescriptionId, Guid ProviderId, DateOnly ServiceDate,
    ClaimCodeSystem CodeSystem, string Code, decimal? ContractTariff);

/// <summary>Resolves the authorized underlying order/prescription for a reimbursement (seam to approvals/orders/pharmacy,
/// 36 §5 checks 3/4). An empty result means no authorized service exists → the request goes to ManualAssessment and can
/// never auto-match. More than one candidate ⇒ ambiguous ⇒ ManualAssessment.</summary>
public interface IAuthorizedServiceResolver
{
    Task<IReadOnlyList<AuthorizedService>> ResolveAsync(
        Guid beneficiaryId, Guid? orderId, Guid? prescriptionId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Default resolver: finds NOTHING, so every reimbursement lands in ManualAssessment until the
/// approvals/orders/pharmacy authorization-query wiring is live. Never invents an authorization.</summary>
public sealed class NoAuthorizedServiceResolver : IAuthorizedServiceResolver
{
    public Task<IReadOnlyList<AuthorizedService>> ResolveAsync(
        Guid beneficiaryId, Guid? orderId, Guid? prescriptionId, string? bearerToken, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuthorizedService>>([]);
}
