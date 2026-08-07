namespace Mersal.ClinicalCodes;

/// <summary>
/// The platform's single implementation of ICD-10 code normalisation and hierarchy comparison.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes of the same code circulate: <c>emr.diagnosis</c> records the DOTTED specific code ("E11.9"),
/// and <c>masterdata.drug_indication</c> holds an UNDOTTED 3-character category ("E11") because every one of
/// the 874 codes in the Egyptian drug workbook is a category. Comparing them by equality reports a mismatch
/// on virtually every prescription, and a warning that always fires is one clinicians learn to click past.
/// </para>
/// <para>
/// Before phase 28 the normalisation lived in two places — <c>MasterDataNormalize.IcdCategory</c> and a
/// private copy inside <c>PrescriptionValidator</c>. Both were correct on the day they were written, which
/// is exactly how a duplicated rule survives long enough to diverge.
/// </para>
/// </remarks>
public static class IcdCodes
{
    /// <summary>Trim and upper-case, dotted form preserved: "e11.9" → "E11.9".</summary>
    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    /// <summary>
    /// The 3-character category of a code — "E11.9" → "E11", "E119" → "E11", "E11" → "E11".
    /// </summary>
    /// <remarks>
    /// Retained because the drug workbook records indications at exactly this level. It is the FALLBACK
    /// comparison, used where no hierarchy row exists; the real rule is
    /// <see cref="IsDescendantOrSelf"/> against the loaded parent chain.
    /// </remarks>
    public static string Category(string code)
    {
        var c = Normalize(code);
        // The dot is positional, never a separator to split on: "E11.9" and "E119" are the same code, and
        // the category is the first three characters of either.
        var undotted = c.Replace(".", "", StringComparison.Ordinal);
        return undotted.Length <= 3 ? undotted : undotted[..3];
    }

    /// <summary>
    /// Whether <paramref name="diagnosis"/> is the indication node itself or falls underneath it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The matching rule design 44 §6 asks for: an indication at node L matches a diagnosis D when D is a
    /// DESCENDANT-OR-SELF of L. <paramref name="diagnosisAncestors"/> is the chain the catalogue loaded —
    /// subcategory → category → block → chapter — which is what makes a block-level indication ("J00-J06",
    /// acute upper respiratory infections) expressible at all. Truncation cannot express a block, and
    /// truncation is what this replaces.
    /// </para>
    /// </remarks>
    public static bool IsDescendantOrSelf(
        string diagnosis, IReadOnlyCollection<string> diagnosisAncestors, string indicationNode)
    {
        ArgumentNullException.ThrowIfNull(diagnosisAncestors);

        var node = Normalize(indicationNode);
        if (string.Equals(Normalize(diagnosis), node, StringComparison.Ordinal)) return true;
        if (diagnosisAncestors.Any(a => string.Equals(Normalize(a), node, StringComparison.Ordinal))) return true;

        // No hierarchy row for this diagnosis — the catalogue may not have been reloaded since the closure
        // table arrived. Fall back to the category comparison rather than reporting a mismatch, which would
        // warn on nearly every prescription.
        return diagnosisAncestors.Count == 0
               && string.Equals(Category(diagnosis), Category(indicationNode), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the DIAGNOSIS is less specific than the indication — "E11" recorded against an "E11.9"
    /// indication.
    /// </summary>
    /// <remarks>
    /// Neither a clean hit nor a miss, and design 44 §6 insists it is reported as neither. The patient may
    /// well have the more specific condition; nobody has coded it that way. Calling it a match would assert
    /// something the record does not say, and calling it a mismatch would produce an off-label warning on a
    /// prescription that is very likely on-label.
    /// </remarks>
    public static bool IsLessSpecificThan(
        string diagnosis, string indicationNode, IReadOnlyCollection<string> indicationAncestors)
    {
        ArgumentNullException.ThrowIfNull(indicationAncestors);

        var d = Normalize(diagnosis);
        if (string.Equals(d, Normalize(indicationNode), StringComparison.Ordinal)) return false;
        return indicationAncestors.Any(a => string.Equals(Normalize(a), d, StringComparison.Ordinal));
    }
}
