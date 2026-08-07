namespace Mersal.Prescribing;

/// <summary>What a dosage form implies. Nulls mean "not derivable", never a default.</summary>
public sealed record DerivedPackFacts(string? PrescribingUnit, bool? IsPackSplittable);

/// <summary>
/// 29.6 — derives the prescribing unit and splittability from the dosage form (design 45 §6).
///
/// <para><b>A heuristic, explicitly.</b> "`is_pack_splittable` defaults from the dosage form but must be
/// OVERRIDABLE per product — the form is a good heuristic and a poor law." A chewable tablet that must not be
/// halved and a scored one that may be are both "tablet".</para>
///
/// <para><b>An unrecognised form derives NOTHING.</b> The tempting default is <c>splittable = true</c>,
/// because most things are — and it is the dangerous one, because it silently permits a fractional inhaler.
/// Absence is carried through as absence, and the quantity check reports NotChecked naming the field.</para>
/// </summary>
public static class PackUnitRules
{
    /// <summary>Form fragment → (unit, splittable). Matched as a SUBSTRING, because the workbook's forms are
    /// free text: "f.c. tablet", "film coated tablets" and "tablet" are all one thing.</summary>
    private static readonly (string Fragment, string Unit, bool Splittable)[] Forms =
    [
        // Non-splittable FIRST: "pre-filled pen" contains no splittable fragment, but "spray solution" and
        // "nasal drops" could both match a shorter splittable token if the order were reversed.
        ("inhaler", "Puff", false),
        ("puff", "Puff", false),
        ("pen", "IU", false),
        ("vial", "Vial", false),
        ("ampoule", "Ampoule", false),
        ("ampule", "Ampoule", false),
        ("patch", "Patch", false),
        ("spray", "Spray", false),
        ("suppositor", "Suppository", true),
        ("sachet", "Sachet", true),
        ("capsule", "Capsule", true),
        ("caps", "Capsule", true),
        ("tablet", "Tablet", true),
        ("tab", "Tablet", true),
        ("drop", "Drop", true),
        ("syrup", "ML", true),
        ("suspension", "ML", true),
        ("solution", "ML", true),
        ("cream", "Gram", true),
        ("ointment", "Gram", true),
        ("gel", "Gram", true),
    ];

    /// <summary>What this dosage form implies, or nulls when it implies nothing.</summary>
    public static DerivedPackFacts FromDosageForm(string? dosageForm)
    {
        if (string.IsNullOrWhiteSpace(dosageForm)) return new DerivedPackFacts(null, null);

        var form = dosageForm.Trim().ToLowerInvariant();
        foreach (var (fragment, unit, splittable) in Forms)
        {
            if (form.Contains(fragment, StringComparison.Ordinal))
                return new DerivedPackFacts(unit, splittable);
        }

        // Unrecognised. NOT defaulted — see the class remarks.
        return new DerivedPackFacts(null, null);
    }

    /// <summary>The derived facts, with any product-level statement taking precedence. The override always
    /// wins: the form is a heuristic and the product's own data is not.</summary>
    public static DerivedPackFacts Resolve(string? form, bool? statedSplittable, string? statedUnit = null)
    {
        var derived = FromDosageForm(form);
        return new DerivedPackFacts(
            statedUnit ?? derived.PrescribingUnit,
            statedSplittable ?? derived.IsPackSplittable);
    }

    /// <summary>
    /// Whether all three facts a quantity calculation needs are known.
    /// </summary>
    /// <remarks>
    /// ALL three, not any: a row with a unit and a splittability but no pack size still cannot be converted
    /// into whole packs, and reporting it as complete would produce exactly the confident wrong number
    /// invariant 8 forbids.
    /// </remarks>
    public static bool IsComplete(string? unit, decimal? packSize, bool? splittable) =>
        !string.IsNullOrWhiteSpace(unit) && packSize is > 0 && splittable is not null;
}
