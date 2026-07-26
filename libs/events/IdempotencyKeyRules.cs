using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mersal.Events;

/// <summary>
/// Phase 18.A3 (audit R2) — shared rules for caller-supplied <c>Idempotency-Key</c> headers on the
/// platform's must-not-double-apply endpoints (consume, dispense).
///
/// Two defects this closes:
/// <list type="number">
/// <item><b>Prefix collisions.</b> orders composes a per-line key as <c>key + "::" + lineId</c> and
/// matched replays with <c>StartsWith(key + "::")</c>. Nothing stopped a caller putting <c>::</c> in
/// the header, so key <c>A</c> could false-replay rows written by key <c>A::L</c>. The separator is now
/// reserved: a header containing it is rejected, which makes the prefix unambiguous by construction.</item>
/// <item><b>No payload binding.</b> A replayed key returned the ORIGINAL rows even when the body had
/// changed, so a client that corrected a quantity and reused the key believed work had been done that
/// never happened. Callers now store <see cref="Hash"/> of the canonical request alongside the key and
/// reject a replay whose hash differs.</item>
/// </list>
/// </summary>
public static class IdempotencyKeyRules
{
    /// <summary>Reserved separator for composed per-line keys. Never legal inside a caller's header.</summary>
    public const string Separator = "::";

    /// <summary>Upper bound matching the persisted column width, so an over-long key fails at the edge
    /// with a clear 400 rather than at the database.</summary>
    public const int MaxLength = 80;

    /// <summary>Null when the header is acceptable, else a short problem token for the edge to surface.</summary>
    public static string? Validate(string? idempotencyKey) => idempotencyKey switch
    {
        null or "" => "idempotency-key-required",
        var k when string.IsNullOrWhiteSpace(k) => "idempotency-key-required",
        var k when k.Contains(Separator, StringComparison.Ordinal) => "idempotency-key-reserved-characters",
        var k when k.Length > MaxLength => "idempotency-key-too-long",
        _ => null,
    };

    /// <summary>Stable SHA-256 (hex) over the parts that define a request. Callers pass an ORDER-STABLE
    /// sequence — sort collections before hashing, so two orderings of the same work hash alike.</summary>
    public static string Hash(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var canonical = string.Join('\u001f', parts);   // ASCII unit separator: cannot occur in ids or numbers
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Canonical rendering of a decimal for hashing — invariant culture, no trailing-zero drift.</summary>
    public static string Amount(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>True when a stored hash proves the replay is the SAME request. A null stored hash is a
    /// row written before the column existed: unverifiable, so treated as a match (behaviour unchanged
    /// for legacy rows rather than newly rejecting them).</summary>
    public static bool Matches(string? storedHash, string currentHash) =>
        storedHash is null || string.Equals(storedHash, currentHash, StringComparison.Ordinal);
}
