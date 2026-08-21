using System.Text;

namespace Mersal.Eligibility.Domain;

/// <summary>
/// Does the name the desk was given corroborate the record the identifier resolved to?
/// </summary>
/// <remarks>
/// <para><b>What this is for.</b> The eligibility screen used to search on one free-text box — a card number,
/// an ID, or any fragment of a name — and then run the check against the FIRST hit. "Ahmed" returned every
/// Ahmed on the platform and the desk was shown one of them, with no indication that there had been others.
/// The plan, the remaining cap and the visit verdict on screen belonged to a person nobody had chosen.</para>
///
/// <para><b>What it is NOT.</b> This is corroboration, not authentication. It stops the WRONG RECORD being
/// opened; it does not prove the person at the desk is the person on the card. Nothing here should be
/// described as identity verification in the security sense, and no downstream decision may lean on it as
/// though it were.</para>
///
/// <para><b>Why a pure function.</b> The rule is the whole feature. Every interesting case — a fragment that
/// is too short, a match on the family name only, a hyphenated or prefixed Arabic name, a middle name the
/// record does not carry — is a question about this method and nothing else, so it is testable without a
/// database, an HTTP host or a fixture.</para>
/// </remarks>
public static class IdentityCorroboration
{
    /// <summary>
    /// Below this, a fragment stops narrowing anything.
    /// </summary>
    /// <remarks>
    /// A single letter prefix-matches a large fraction of any name list, so accepting one would restore the
    /// defect with an extra keystroke. Two is the floor at which the fragment is doing work; the caller is
    /// free to ask for more.
    /// </remarks>
    public const int MinimumFragment = 2;

    /// <summary>
    /// True when every term offered prefix-matches some token of the recorded name.
    /// </summary>
    /// <remarks>
    /// <para><b>Prefix, not contains.</b> "part of the name" at a desk means the beginning of one of its
    /// words — that is how a name is read off a card or spelled out over a counter. `Contains` would let a
    /// two-letter fragment land in the middle of an unrelated name ("li" inside "Khalil"), which is close
    /// enough to matching anything that it would not be a check.</para>
    ///
    /// <para><b>Every term, not any.</b> Typing more must narrow, never widen. If "Ahmed Sayed" matched on
    /// "Ahmed" alone, adding the family name would make a wrong record easier to open rather than harder.</para>
    ///
    /// <para><b>Tokens split on spaces AND hyphens.</b> "Al-Sayed" is one word with two parts, and a desk
    /// given "Sayed" is right to expect it to match. Splitting only on whitespace would refuse a correct
    /// name and send the operator looking for a fault that is not there.</para>
    /// </remarks>
    public static bool NameCorroborates(string? givenName, string? familyName, string? offered)
    {
        var terms = Tokens(offered);
        if (terms.Count == 0 || terms.Any(t => t.Length < MinimumFragment)) return false;

        var recorded = Tokens($"{givenName} {familyName}");
        if (recorded.Count == 0) return false;

        return terms.All(t => recorded.Any(r => r.StartsWith(t, StringComparison.Ordinal)));
    }

    /// <summary>Whether the fragment is long enough to be asked at all — separated so the SCREEN and the
    /// SERVICE can refuse for the same reason, in the same words, rather than one of them silently
    /// accepting what the other rejects.</summary>
    public static bool IsUsableFragment(string? offered)
    {
        var terms = Tokens(offered);
        return terms.Count > 0 && terms.All(t => t.Length >= MinimumFragment);
    }

    /// <summary>
    /// Casefold and split. `ToLowerInvariant` rather than a culture-sensitive lower: the Turkish dotless-i
    /// rule would fold "I" to "ı" under a tr-TR request culture and stop an English name matching itself.
    /// </summary>
    private static List<string> Tokens(string? s) =>
        (s ?? "")
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Split([' ', '-', '‐', '‑', '\t', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
