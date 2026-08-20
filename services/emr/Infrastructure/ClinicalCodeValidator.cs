namespace Mersal.Emr.Infrastructure;

/// <summary>Validates clinical codes against masterdata-service (phase 0b): ICD-10 (diagnosis), the allergen
/// catalogue, drugs, and optional LOINC on vitals. The HTTP implementation lives in the Api layer (it needs an
/// HttpClient + the caller's bearer token) and caches lookups; tests inject an in-memory validator. Writes
/// FAIL CLOSED — if masterdata is unreachable the validator surfaces the failure so the endpoint rejects the
/// write rather than persisting an unvalidated code.</summary>
public interface IClinicalCodeValidator
{
    Task<bool> IcdExistsAsync(string icdCode, string? bearerToken, CancellationToken ct = default);
    /// <summary>
    /// Resolve an allergen to its catalogue NAME, or null when masterdata does not hold it.
    ///
    /// <para>This replaced an <c>AllergenExistsAsync</c> boolean. The endpoint needs both facts — is this a
    /// real allergen, and what is it called — and asking for the weaker one meant the name was never
    /// captured, which is how a display field that three readers already expected came to be permanently
    /// empty. Returning the name answers existence too: null is "no".</para>
    /// </summary>
    Task<string?> AllergenNameAsync(Guid allergenId, string? bearerToken, CancellationToken ct = default);
    Task<bool> DrugExistsAsync(Guid drugId, string? bearerToken, CancellationToken ct = default);

    /// <summary>
    /// Resolve a drug to its catalogue NAME, or null when masterdata does not hold it.
    /// </summary>
    /// <remarks>
    /// The same lesson <see cref="AllergenNameAsync"/> records, in the table next door: asking
    /// <see cref="DrugExistsAsync"/> for the weaker fact is exactly why medication_history never captured a
    /// name, and why its two readers — the encounter list and the prescribing interaction warning — would
    /// otherwise show a uuid to a clinician mid-decision. Returning the name answers existence too: null
    /// is "no".
    /// </remarks>
    Task<string?> DrugNameAsync(Guid drugId, string? bearerToken, CancellationToken ct = default);
    /// <summary>LOINC is optional on a vital; when omitted this returns true. When present it is validated
    /// (currently accepted-and-recorded — no LOINC dataset is loaded yet — documented like provider-service).</summary>
    Task<bool> LoincValidAsync(string? loincCode, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Accepts everything — the default used in unit tests and when masterdata validation is disabled.</summary>
public sealed class AllowAllClinicalCodeValidator : IClinicalCodeValidator
{
    public Task<bool> IcdExistsAsync(string icdCode, string? bearerToken, CancellationToken ct = default) => Task.FromResult(true);
    public Task<string?> AllergenNameAsync(Guid allergenId, string? bearerToken, CancellationToken ct = default) => Task.FromResult<string?>("Test allergen");
    public Task<bool> DrugExistsAsync(Guid drugId, string? bearerToken, CancellationToken ct = default) => Task.FromResult(true);
    public Task<string?> DrugNameAsync(Guid drugId, string? bearerToken, CancellationToken ct = default) => Task.FromResult<string?>("Test drug");
    public Task<bool> LoincValidAsync(string? loincCode, string? bearerToken, CancellationToken ct = default) => Task.FromResult(true);
}
