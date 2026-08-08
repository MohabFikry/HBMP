using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Mersal.Ingredients;

namespace Mersal.ClinicalValidation;

/// <summary>
/// One drug's manufacturer label, as retrieved from an external label source (openFDA).
/// </summary>
/// <param name="SearchedIngredient">The ingredient name we asked for, after normalisation.</param>
/// <param name="MatchedGenericName">
/// The generic name the label is actually <b>for</b>, exactly as the source returned it. This is not
/// decoration: it is the evidence that the right label came back. A search for "amoxicillin" returns the
/// amoxicillin/clavulanate combination label ahead of the plain one, and a search for "chloride" returns
/// <i>benzalkonium</i> chloride — so a retrieval that does not carry what it matched cannot be audited, and
/// reading an interaction off the wrong molecule's label is worse than not checking at all.
/// </param>
/// <param name="Aliases">
/// Every name another label might refer to this drug by — the searched name, the matched generic name, and
/// their INN/USAN counterparts. Scanning for only one spelling is how "paracetamol" misses a label that says
/// "acetaminophen".
/// </param>
/// <param name="LabelVersion">The SPL version the answer came from, for provenance.</param>
public sealed record DrugLabelFact(
    Guid DrugId,
    string SearchedIngredient,
    string MatchedGenericName,
    IReadOnlyList<string> Aliases,
    string? InteractionsText,
    string? DosingText,
    string? StrengthsText,
    string LabelVersion);

/// <summary>
/// The manufacturer-label evidence gathered for one validation run.
/// </summary>
/// <param name="Unmatched">
/// Drugs for which no label exists to retrieve, and why. "This product has no recorded active ingredient",
/// "no label is published under that ingredient" and "the ingredient matched products but none exactly" are
/// three different statements, and all three are honest answers that a single missing key would flatten into
/// silence. These render as <see cref="ClinicalState.NotChecked"/>.
/// </param>
/// <param name="Failed">
/// Drugs whose lookup <b>failed</b> — timeout, rate limit, transport error, unparseable response.
/// </param>
/// <remarks>
/// <see cref="Unmatched"/> and <see cref="Failed"/> are separate buckets because they are separate facts:
/// "there is no such label" is an answer, and "we could not find out" is not. Merging them would let an
/// openFDA outage render as the same quiet "not checked" a genuinely unlisted product produces, which is the
/// precise failure mode <see cref="Fetched{T}"/> exists to prevent — one bucket down means
/// <see cref="ClinicalState.Unavailable"/>, and the prescriber is told the source failed.
/// </remarks>
public sealed record LabelEvidence(
    IReadOnlyDictionary<Guid, DrugLabelFact> ByDrug,
    IReadOnlyDictionary<Guid, string> Unmatched,
    IReadOnlyDictionary<Guid, string> Failed);

/// <summary>A drug named in another drug's label interactions section, with the sentence that named it.</summary>
public sealed record LabelMention(string Term, string Sentence);

/// <summary>
/// Scans one drug's label interactions section for a mention of another drug.
/// </summary>
/// <remarks>
/// <para>
/// Pure text matching over prose, and it is worth being exact about what that can and cannot establish.
/// A label's interactions section is a <b>narrative</b>, not a pair list: openFDA publishes no structured
/// drug-drug interaction data, and the platform's own local list holds zero pairs. So a mention is real
/// evidence — warfarin's label names amiodarone, fluconazole and ibuprofen — but the <i>absence</i> of a
/// mention establishes almost nothing, which is why a clean scan reports <c>NotChecked</c> and never
/// <c>Ok</c>.
/// </para>
/// <para>
/// The matched sentence is returned rather than a verdict. The prescriber is the one who can tell "may
/// increase bleeding risk" from "no interaction was observed", and handing them the manufacturer's own
/// wording is both more useful and more honest than a severity this code is not entitled to assign.
/// </para>
/// </remarks>
public static class LabelInteractionScan
{
    /// <summary>Long enough to carry the claim, short enough to read mid-consultation.</summary>
    private const int MaxSentenceLength = 320;

    public static LabelMention? Find(string? interactionsText, IReadOnlyList<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        if (string.IsNullOrWhiteSpace(interactionsText)) return null;

        foreach (var alias in aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            // Whole-word only. A substring match has "iron" firing on "environment" and "ergotamine" on
            // "ergotamine tartrate"'s neighbours — noise that would train prescribers to dismiss the check.
            var match = Regex.Match(
                interactionsText,
                $@"\b{Regex.Escape(alias.Trim())}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));

            if (match.Success) return new LabelMention(alias.Trim(), Sentence(interactionsText, match.Index));
        }

        return null;
    }

    /// <summary>The sentence containing the match, trimmed to something readable.</summary>
    private static string Sentence(string text, int index)
    {
        var start = text.LastIndexOfAny(['.', '\n'], Math.Min(index, text.Length - 1));
        start = start < 0 ? 0 : start + 1;

        var end = text.IndexOf('.', Math.Min(index, text.Length - 1));
        end = end < 0 ? text.Length : end + 1;

        var sentence = text[start..end].Trim();
        if (sentence.Length > MaxSentenceLength) sentence = sentence[..MaxSentenceLength].TrimEnd() + "…";

        return sentence;
    }
}
