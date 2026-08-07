using System.Globalization;
using Mersal.ClinicalCodes;
using System.Text;

namespace Mersal.MasterData.Domain;

/// <summary>Canonicalization + validation helpers used by both the loaders and the service.</summary>
public static class MasterDataNormalize
{
    /// <summary>Trim + upper-case an ICD-10 code (dotted format preserved, e.g. "e11.9" → "E11.9").</summary>
    /// <remarks>
    /// Delegates to <see cref="IcdCodes"/>, which is the platform's ONE implementation (design 44 §6). It
    /// used to be defined here and copied again inside PrescriptionValidator; two implementations of a
    /// matching rule diverge, and the divergence surfaces as an indication check disagreeing with the
    /// catalogue that fed it rather than as a failing test.
    /// </remarks>
    public static string Icd(string raw) => IcdCodes.Normalize(raw);

    /// <summary>
    /// The 3-character ICD-10 <b>category</b> of a code — "E11.9" → "E11", "E119" → "E11", "E11" → "E11".
    /// </summary>
    /// <remarks>
    /// Drug indications are recorded at category level (every code in the Egyptian drug list is a
    /// 3-character category), while a recorded diagnosis is specific. Comparing the two by equality makes
    /// the indication check report "not a listed indication" on nearly every prescription — a warning that
    /// always fires is a warning clinicians learn to dismiss. Both sides go through here before comparison.
    /// </remarks>
    public static string IcdCategory(string raw) => IcdCodes.Category(raw);

    /// <summary>
    /// A deterministic uuid for a drug, derived from its id in the source file.
    /// </summary>
    /// <remarks>
    /// The loader previously minted <c>Guid.NewGuid()</c> per row and relied on the upsert matching an
    /// existing <c>drug_code</c> to preserve ids. That holds only while the trade-name text is byte-stable:
    /// any drift in the source spelling mints a fresh uuid and orphans every drug_indication, interaction
    /// and prescription line pointing at the old one. Deriving the id from the source row id makes a reload
    /// stable by construction instead of by luck.
    /// <para>
    /// Namespaced-and-hashed in the shape of RFC 9562 §5.8 (version 8, custom). SHA-256 is used as a
    /// distribution function over an identifier here, not as a security primitive.
    /// </para>
    /// </remarks>
    public static Guid DrugId(string sourceRowId)
    {
        // Namespace prefix keeps drug ids disjoint from any other id space derived the same way.
        var input = Encoding.UTF8.GetBytes($"mersal:masterdata:drug:{sourceRowId.Trim()}");
        var hash = System.Security.Cryptography.SHA256.HashData(input);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);   // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant

        // Guid(byte[]) reads the first three groups little-endian on all platforms; build the fields
        // explicitly so the same input yields the same uuid text everywhere.
        var a = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        var b = (short)((bytes[4] << 8) | bytes[5]);
        var c = (short)((bytes[6] << 8) | bytes[7]);
        return new Guid(a, b, c, bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);
    }

    /// <summary>Trim + upper-case a CPT code.</summary>
    public static string Cpt(string raw) => raw.Trim().ToUpperInvariant();

    /// <summary>Trim + upper-case an ATC code.</summary>
    public static string Atc(string raw) => raw.Trim().ToUpperInvariant();

    /// <summary>
    /// ATC level from code length: A(1)=1, A10(3)=2, A10B(4)=3, A10BA(5)=4, A10BA02(7)=5.
    /// Returns 0 for an unrecognized length.
    /// </summary>
    public static int AtcLevel(string atcCode) => atcCode.Trim().Length switch
    {
        1 => 1, 3 => 2, 4 => 3, 5 => 4, 7 => 5, _ => 0,
    };

    /// <summary>All ancestor ATC codes of a full code by truncation (e.g. A10BA02 → A, A10, A10B, A10BA).</summary>
    public static IEnumerable<string> AtcAncestors(string atcCode)
    {
        var c = Atc(atcCode);
        foreach (var len in new[] { 1, 3, 4, 5 })
        {
            if (c.Length > len) yield return c[..len];
        }
    }

    /// <summary>
    /// A stable natural key for a drug from its commercial name: upper-case, collapse whitespace,
    /// strip most punctuation. Deterministic so re-loads dedupe on the same key.
    /// </summary>
    public static string DrugCode(string commercialName)
    {
        var sb = new StringBuilder(commercialName.Length);
        var lastSpace = false;
        foreach (var ch in commercialName.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch); lastSpace = false;
            }
            else if (!lastSpace && sb.Length > 0)
            {
                sb.Append('-'); lastSpace = true;
            }
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 128 ? s[..128] : s;
    }

    /// <summary>Parse a decimal price, tolerant of blanks/locale.</summary>
    public static decimal? Price(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}
