using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Mersal.Ingredients;

/// <summary>
/// Normalises the Egyptian catalogue's <c>scientific_name</c> into ingredient names a label source will
/// recognise, and back again.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separated from the HTTP client on purpose: every dangerous failure of this feature is a
/// <i>naming</i> failure, not a transport failure, so the naming has to be unit-testable without a network.
/// </para>
/// <para>
/// The catalogue is not tidy. Real values include <c>amoxicillin+clavulanic acid</c> (a combination),
/// <c>paracetamol(acetaminophen)</c> (INN with the USAN in brackets), <c>diclofenac sodium</c> (a salt) and
/// <c>sun protection formula</c> (not an ingredient at all).
/// </para>
/// </remarks>
public static partial class IngredientTokens
{
    /// <summary>
    /// Salt and hydrate forms, stripped only from the <b>end</b> of a name.
    /// </summary>
    /// <remarks>
    /// Suffix-only, and that is the whole point. Stripping these anywhere turns "sodium chloride" into
    /// "chloride", which matches benzalkonium chloride — a disinfectant — and would have the platform read
    /// interaction advice for the wrong substance off a confidently-retrieved label.
    /// </remarks>
    [StringSyntax(StringSyntaxAttribute.Regex)]
    private const string SaltSuffix =
        @"\s+(sodium|potassium|hydrochloride|hcl|hydrobromide|sulfate|sulphate|maleate|besylate|besilate"
        + @"|tartrate|succinate|mesylate|mesilate|citrate|acetate|fumarate|phosphate|nitrate|gluconate"
        + @"|calcium|magnesium|dihydrate|monohydrate|trihydrate|anhydrous)$";

    /// <summary>
    /// International (INN) names against the United States (USAN) names FDA labels are written in.
    /// </summary>
    /// <remarks>
    /// Not a nicety. Egypt prescribes in INN, and <c>paracetamol</c>, <c>salbutamol</c> and
    /// <c>adrenaline</c> — three of the most-dispensed medicines on the formulary — all return nothing from
    /// openFDA under their INN spelling. Without this map the check would silently report "no label
    /// published" for a large part of the catalogue and look like a coverage gap rather than a bug.
    /// </remarks>
    private static readonly (string Inn, string Usan)[] InnUsan =
    [
        ("paracetamol", "acetaminophen"),
        ("salbutamol", "albuterol"),
        ("adrenaline", "epinephrine"),
        ("noradrenaline", "norepinephrine"),
        ("lignocaine", "lidocaine"),
        ("frusemide", "furosemide"),
        ("rifampicin", "rifampin"),
        ("ciclosporin", "cyclosporine"),
        ("glibenclamide", "glyburide"),
        ("pethidine", "meperidine"),
        ("amoxycillin", "amoxicillin"),
        ("oestradiol", "estradiol"),
        ("dothiepin", "dosulepin"),
        ("trimethoprim-sulfamethoxazole", "sulfamethoxazole and trimethoprim"),
        ("beclometasone", "beclomethasone"),
        ("chlorphenamine", "chlorpheniramine"),
        ("hyoscine", "scopolamine"),
        ("phenobarbitone", "phenobarbital"),
        ("thyroxine", "levothyroxine"),
    ];

    /// <summary>Lower-cases, collapses whitespace and removes a trailing salt or hydrate form.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var t = Whitespace().Replace(name.Trim().ToLowerInvariant(), " ");
        string previous;
        do
        {
            previous = t;
            t = SaltSuffixPattern().Replace(t, string.Empty).Trim();
        }
        while (previous != t);

        return t;
    }

    /// <summary>Both spellings of a name — the INN and the USAN — whichever was supplied.</summary>
    public static IReadOnlyList<string> Synonyms(string name)
    {
        var n = Normalize(name);
        var names = new List<string> { n };

        foreach (var (inn, usan) in InnUsan)
        {
            if (string.Equals(n, inn, StringComparison.Ordinal)) names.Add(usan);
            else if (string.Equals(n, usan, StringComparison.Ordinal)) names.Add(inn);
        }

        return names;
    }

    /// <summary>
    /// The ingredient names to try against a label source, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>unmodified</b> name is always tried before the salt-stripped one. "Sodium chloride" and
    /// "warfarin sodium" are both real label names; trying the stripped form first would answer the second
    /// correctly and the first with a different compound entirely.
    /// </para>
    /// <para>
    /// A combination such as <c>amoxicillin+clavulanic acid</c> yields each component separately, because a
    /// combination's interactions are the union of its parts' and no label is published under the joined
    /// string.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Candidates(string? scientificName)
    {
        if (string.IsNullOrWhiteSpace(scientificName)) return [];

        var ordered = new List<string>();

        foreach (var component in Splitter().Split(scientificName.ToLowerInvariant()))
        {
            var part = component.Trim();
            if (part.Length == 0) continue;

            // "paracetamol(acetaminophen)": the bracketed name is the one FDA publishes under, so it goes
            // first. The catalogue supplies this pairing for exactly the drugs where the spellings diverge.
            var bracketed = Bracketed().Match(part);
            var raw = bracketed.Success
                ? new[] { bracketed.Groups[2].Value, bracketed.Groups[1].Value }
                : [part];

            foreach (var name in raw)
            {
                foreach (var candidate in Synonyms(name).Prepend(Whitespace().Replace(name.Trim(), " ")))
                {
                    if (candidate.Length > 0 && !ordered.Contains(candidate, StringComparer.Ordinal))
                    {
                        ordered.Add(candidate);
                    }
                }
            }
        }

        return ordered;
    }

    /// <summary>
    /// The molecules a product is made of, as canonical <c>ingredient_key</c> values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT <see cref="Candidates"/>. That method is searching an external label source, so it
    /// returns every spelling worth trying, best first — the INN, the USAN, the unstripped form. This is
    /// choosing a single canonical KEY per component, which is a different question with a different answer:
    /// returning both spellings here would put "paracetamol" and "acetaminophen" in the catalogue as two
    /// different molecules, and a rule written against one would miss every product recorded under the other.
    /// </para>
    /// <para>
    /// The INN wins, because Egypt prescribes in INN and the curated clinical rules are written that way.
    /// A combination yields one key per component — which is what makes co-amoxiclav screen as amoxicillin
    /// and the paracetamol inside a cold remedy findable at all.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Components(string? scientificName)
    {
        if (string.IsNullOrWhiteSpace(scientificName)) return [];

        var keys = new List<string>();

        foreach (var component in Splitter().Split(scientificName.ToLowerInvariant()))
        {
            var part = component.Trim();
            if (part.Length == 0) continue;

            // "paracetamol(acetaminophen)": take the OUTER name. Candidates() prefers the bracketed USAN
            // because an FDA label is published under it; the catalogue's own key should be the name the
            // prescriber and the pharmacist use.
            var bracketed = Bracketed().Match(part);
            var key = Canonical(bracketed.Success ? bracketed.Groups[1].Value : part);

            // The catalogue is not tidy: "sun protection formula" and bare numbers appear in this column.
            // A junk key costs nothing clinically — no rule will ever match it — but it should not be long
            // enough to be a sentence or short enough to be a stray letter.
            if (key.Length is < 3 or > 100) continue;
            if (!key.Any(char.IsLetter)) continue;

            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
        }

        return keys;
    }

    /// <summary>
    /// The one spelling this platform records a molecule under: normalised, and INN rather than USAN.
    /// </summary>
    /// <remarks>
    /// Without this, "acetaminophen" and "paracetamol" are two rows in <c>ingredient</c> and a duplicate-
    /// therapy check comparing them finds nothing — which is precisely the overdose the check exists for.
    /// </remarks>
    public static string Canonical(string name)
    {
        var n = Orthography(Normalize(name));
        foreach (var (inn, usan) in InnUsan)
        {
            if (string.Equals(n, Orthography(usan), StringComparison.Ordinal)) return Orthography(inn);
        }
        return n;
    }

    /// <summary>
    /// British (BAN) spellings folded to the INN forms the curated clinical rules are written in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not cosmetic, and not hypothetical. The Egyptian catalogue writes <c>amoxycillin</c> on 220 products
    /// and <c>amoxicillin</c> on none; it writes <c>sulphamethoxazole</c> on 21 and <c>sulfamethoxazole</c>
    /// on one. Without this fold, a penicillin-allergy mapping written against "amoxicillin" matches nothing
    /// in the catalogue, an interaction rule and the product it should fire on are two different molecules,
    /// and the duplicate-therapy check compares two spellings of one drug and finds no duplication.
    /// </para>
    /// <para>
    /// Every rule here is a documented BAN→INN orthography change, verified against what the catalogue
    /// actually contains rather than guessed: <c>ph</c>→<c>f</c> in sulfonamides, <c>y</c>→<c>i</c> in
    /// amoxicillin, and the <c>ceph</c>→<c>cef</c> prefix (the catalogue already writes every other
    /// cephalosporin as <c>cef</c>; only <c>cephradine</c> lags, and <c>cefradine</c> is its INN).
    /// </para>
    /// <para>
    /// Deliberately NOT a general phonetic normaliser. Folding every <c>ph</c> to <c>f</c> would rewrite
    /// morphine, phenytoin and amphotericin into molecules that do not exist.
    /// </para>
    /// </remarks>
    private static string Orthography(string normalised)
    {
        var n = normalised
            .Replace("sulph", "sulf", StringComparison.Ordinal)
            .Replace("amoxy", "amoxi", StringComparison.Ordinal);

        // Prefix-anchored: "cephradine" is a cephalosporin, "cephalic" would not be an ingredient at all.
        return n.StartsWith("ceph", StringComparison.Ordinal) ? string.Concat("cef", n.AsSpan(4)) : n;
    }

    /// <summary>
    /// Whether a label the source returned is genuinely the label for the ingredient we asked for.
    /// </summary>
    /// <remarks>
    /// Equality after normalisation, not "contains". A contains-match accepts <c>AMOXICILLIN AND
    /// CLAVULANATE POTASSIUM</c> for a search of "amoxicillin", and the combination product's interactions
    /// section is not the plain product's. When nothing matches exactly the honest answer is that the drug
    /// was not checked — never the nearest label.
    /// </remarks>
    public static bool IsExactMatch(string searched, string returnedGenericName)
    {
        var wanted = Synonyms(searched);
        var got = Normalize(returnedGenericName);
        return wanted.Contains(got, StringComparer.Ordinal);
    }

    [GeneratedRegex(SaltSuffix, RegexOptions.CultureInvariant)]
    private static partial Regex SaltSuffixPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>Combination separators. Commas are NOT included — several ingredient names contain one.</summary>
    [GeneratedRegex(@"[+/]", RegexOptions.CultureInvariant)]
    private static partial Regex Splitter();

    [GeneratedRegex(@"^([^(]+)\(([^)]+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Bracketed();
}