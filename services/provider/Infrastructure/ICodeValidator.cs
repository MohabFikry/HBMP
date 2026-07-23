using Mersal.Provider.Domain;

namespace Mersal.Provider.Infrastructure;

/// <summary>Validates a service-line code against masterdata-service (phase 0b). CPT/LOINC codes must
/// resolve; LOCAL codes are free-text (recorded, never validated). The HTTP implementation lives in the
/// Api layer (it needs an HttpClient); tests inject an in-memory validator.</summary>
public interface ICodeValidator
{
    /// <summary>True when <paramref name="code"/> is acceptable for <paramref name="system"/>.</summary>
    Task<bool> IsValidAsync(CodeSystem system, string code, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Accepts everything — the default used in tests and when masterdata validation is disabled.</summary>
public sealed class AllowAllCodeValidator : ICodeValidator
{
    public Task<bool> IsValidAsync(CodeSystem system, string code, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(true);
}
