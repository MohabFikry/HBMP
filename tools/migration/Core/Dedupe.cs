namespace Mersal.Migration.Core;

public enum MatchDecision { AutoMerge, Review, NoMatch }

/// <summary>A beneficiary row being considered for import, in normalized form.</summary>
public sealed record DedupeCandidate(string SourceId, NormalizedIdentifier Identifier, string FullName, DateOnly? BirthDate);

/// <summary>An already-known person to match against (existing store or earlier in this batch).</summary>
public sealed record KnownPerson(string Id, IReadOnlyList<string> IdentifierKeys, string FullName, DateOnly? BirthDate);

/// <summary>
/// Deterministic + fuzzy beneficiary matcher (phase 12.1 STREAM C). Deterministic identifier
/// equality auto-merges; otherwise a name+DOB similarity score routes the pair. The hard rule the
/// acceptance test asserts: a low/medium-confidence pair is NEVER auto-merged — it is routed to the
/// review queue for human sign-off. Only an exact identifier hit or a very-high name score with a
/// matching birth date auto-merges.
/// </summary>
public static class Dedupe
{
    public const double AutoMergeNameScore = 0.92;
    public const double ReviewFloor = 0.75;

    public static DedupeOutcome Match(DedupeCandidate candidate, IReadOnlyList<KnownPerson> known)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(known);

        // 1. Deterministic: an exact identifier hit is an unambiguous merge.
        if (candidate.Identifier.IsValid)
        {
            var key = candidate.Identifier.Key;
            var exact = known.FirstOrDefault(k => k.IdentifierKeys.Contains(key, StringComparer.Ordinal));
            if (exact is not null)
                return new DedupeOutcome(candidate.SourceId, exact.Id, 1.0, MatchDecision.AutoMerge, "identifier-exact");
        }

        // 2. Fuzzy: best name score across known persons, gated by birth-date agreement.
        var candName = NormalizeName(candidate.FullName);
        KnownPerson? best = null;
        var bestScore = 0.0;
        foreach (var k in known)
        {
            var score = JaroWinkler(candName, NormalizeName(k.FullName));
            if (score > bestScore) { bestScore = score; best = k; }
        }

        if (best is null || bestScore < ReviewFloor)
            return new DedupeOutcome(candidate.SourceId, null, bestScore, MatchDecision.NoMatch, "below-review-floor");

        var dobAgrees = candidate.BirthDate is not null && candidate.BirthDate == best.BirthDate;

        // Auto-merge only on a very-high name score AND an agreeing birth date. Everything else —
        // strong name but no DOB agreement, or a mid-band score — is REVIEW, never an auto-merge.
        if (bestScore >= AutoMergeNameScore && dobAgrees)
            return new DedupeOutcome(candidate.SourceId, best.Id, bestScore, MatchDecision.AutoMerge, "name+dob-high");

        var basis = dobAgrees ? "name-mid+dob" : "name-only-no-dob-agreement";
        return new DedupeOutcome(candidate.SourceId, best.Id, bestScore, MatchDecision.Review, basis);
    }

    internal static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var upper = name.Trim().ToUpperInvariant();
        var chars = upper.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Jaro-Winkler similarity in [0,1] — tolerant of typos/transpositions, prefix-weighted.</summary>
    internal static double JaroWinkler(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

        var jaro = Jaro(a, b);
        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(a.Length, b.Length)); i++)
        {
            if (a[i] == b[i]) prefix++;
            else break;
        }
        return jaro + (prefix * 0.1 * (1 - jaro));
    }

    private static double Jaro(string a, string b)
    {
        var window = Math.Max(a.Length, b.Length) / 2 - 1;
        if (window < 0) window = 0;
        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var matches = 0;

        for (var i = 0; i < a.Length; i++)
        {
            var lo = Math.Max(0, i - window);
            var hi = Math.Min(i + window + 1, b.Length);
            for (var j = lo; j < hi; j++)
            {
                if (bMatched[j] || a[i] != b[j]) continue;
                aMatched[i] = true; bMatched[j] = true; matches++;
                break;
            }
        }
        if (matches == 0) return 0.0;

        double transpositions = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (!bMatched[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }
        transpositions /= 2;

        var m = (double)matches;
        return (m / a.Length + m / b.Length + (m - transpositions) / m) / 3;
    }
}
