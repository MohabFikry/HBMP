namespace Mersal.Interop.Domain.Integration;

/// <summary>A document to run OCR over (bytes + content type + language hint). No PHI is stored in the interop
/// context; the extracted text flows on to the owning service's ingestion.</summary>
public sealed record OcrRequest(byte[] Content, string ContentType, string? LanguageHint);
public sealed record OcrResult(bool Extracted, string? Text, string? Reason);

/// <summary>
/// Document-OCR ingestion hook (35 §10 roadmap). Defined now so an OCR pipeline (Tesseract/Arabic model, a
/// managed service, etc.) can be added later WITHOUT redesign — the ingestion pipeline depends only on this
/// interface. The default is a no-op stub.
/// </summary>
public interface IDocumentOcrProvider
{
    Task<OcrResult> ExtractAsync(OcrRequest request, CancellationToken ct = default);
}

/// <summary>Structured fields an Arabic-NLP extractor might surface from free text (all optional).</summary>
public sealed record NlpExtraction(bool Extracted, IReadOnlyDictionary<string, string> Fields, string? Reason);

/// <summary>
/// Arabic-NLP extraction hook (35 §10 roadmap): extract structured fields (names, dates, identifiers, clinical
/// terms) from Arabic free text. Defined now as an interface with a no-op stub so a real model can be plugged in
/// later behind the same seam.
/// </summary>
public interface IArabicNlpExtractor
{
    Task<NlpExtraction> ExtractAsync(string text, CancellationToken ct = default);
}

/// <summary>No-op OCR provider — the placeholder until a real engine is wired (and DPIA-gated if it calls out).</summary>
public sealed class NoOpDocumentOcrProvider : IDocumentOcrProvider
{
    public Task<OcrResult> ExtractAsync(OcrRequest request, CancellationToken ct = default) =>
        Task.FromResult(new OcrResult(false, null, "OCR not enabled — no provider wired (stub)."));
}

/// <summary>No-op Arabic-NLP extractor — the placeholder until a real model is wired.</summary>
public sealed class NoOpArabicNlpExtractor : IArabicNlpExtractor
{
    public Task<NlpExtraction> ExtractAsync(string text, CancellationToken ct = default) =>
        Task.FromResult(new NlpExtraction(false, new Dictionary<string, string>(), "Arabic-NLP not enabled — no extractor wired (stub)."));
}
