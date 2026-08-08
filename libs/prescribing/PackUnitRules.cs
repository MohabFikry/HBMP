namespace Mersal.Prescribing;

/// <summary>What a product's pack data implies. Nulls mean "not derivable", never a default.</summary>
/// <param name="PackCountsPrescribingUnits">
/// 31.2 — whether <c>pack_size</c> counts the SAME thing <c>PrescribingUnit</c> names.
///
/// <para>It counts the catalogue's MINOR UNITS: 20 tablets, 10 sachets, 3 ampoules — but also 5 PENS, and
/// ONE bottle of a 120 ml syrup. So it lines up with the dose for the countable forms and does not for the
/// measured ones, where the pack counts containers and the dose counts millilitres, grams, puffs or IU.</para>
///
/// <para>Only where it lines up can a box count be computed. Where it does not, the number is withheld:
/// 180 IU over a pack of 5 pens divides to 36 boxes, and 180 IU is less than one 300-IU pen.</para>
/// </param>
public sealed record DerivedPackFacts(
    string? PrescribingUnit, bool? IsPackSplittable, decimal? PackSize = null,
    bool PackCountsPrescribingUnits = false);

/// <summary>
/// 29.6 — derives the prescribing unit, the pack size and splittability (design 45 §6).
///
/// <para><b>Two sources, and they do not rank equally.</b> The catalogue carries a measured pair —
/// <c>Major Units (per box)</c> and <c>Minor Units (total)</c> — and a free-text <c>Dosage Form</c>. The pair
/// is data; the form is a word. So the pair decides <b>splittability and pack size</b>, and the form is left
/// with the one thing it is actually good for: naming the unit the dose is counted in.</para>
///
/// <para><b>Why the form was the wrong source for splittability.</b> It is wrong in both directions on real
/// rows. It calls a box of three ampoules unsplittable, when three ampoules are three separate items and
/// giving one is routine; and it says nothing at all about the 38 dosage forms it does not recognise. The
/// pack columns answer both: 22,649 of 22,653 rows carry them.</para>
///
/// <para><b>An unrecognised form still derives NOTHING.</b> The tempting default is <c>splittable = true</c>,
/// because most things are — and it is the dangerous one, because it silently permits a fractional inhaler.
/// Absence is carried through as absence, and the quantity check reports NotChecked naming the field.</para>
/// </summary>
public static class PackUnitRules
{
    /// <summary>
    /// Units that name a COUNTABLE ITEM — the same thing a box holds a number of.
    /// </summary>
    /// <remarks>
    /// The complement is the measured units: ML, Gram, Puff, IU, Drop, Spray. A box of those counts
    /// CONTAINERS (one bottle, five pens), so dividing a dose total by the pack size mixes two different
    /// things. Kept as a set of UNITS rather than of forms, because the unit is what the dose is expressed
    /// in and it is the unit the comparison is actually about.
    /// </remarks>
    private static readonly HashSet<string> CountableUnits = new(StringComparer.Ordinal)
    {
        "Tablet", "Capsule", "Sachet", "Suppository", "Pessary", "Lozenge", "Gummy",
        "Vial", "Ampoule", "Syringe", "Cartridge", "Patch", "Dressing", "Enema", "Bar",
    };

    /// <summary>Form fragment → (unit, splittable). Matched as a SUBSTRING, because the workbook's forms are
    /// free text: "f.c. tablet", "film coated tablets" and "tablet" are all one thing.</summary>
    /// <remarks>
    /// The <c>Splittable</c> column is now only a FALLBACK, consulted when the pack columns are missing or
    /// incoherent. See <see cref="Resolve"/> for the precedence.
    /// </remarks>
    private static readonly (string Fragment, string Unit, bool Splittable)[] Forms =
    [
        // Non-splittable FIRST: "pre-filled pen" contains no splittable fragment, but "spray solution" and
        // "nasal drops" could both match a shorter splittable token if the order were reversed.
        ("inhaler", "Puff", false),
        ("puff", "Puff", false),
        // "prefilled syringe" / "pre-filled syringe" — before "pen", and before the liquid fragments, because
        // a syringe is the item, not its contents.
        ("syringe", "Syringe", false),
        ("cartridge", "Cartridge", false),
        ("penfill", "IU", false),
        ("pen", "IU", false),
        ("vial", "Vial", false),
        ("ampoule", "Ampoule", false),
        ("ampule", "Ampoule", false),
        ("patch", "Patch", false),
        ("dressing", "Dressing", false),
        // A vaccine is supplied AS a vial, so it needs no unit of its own — reusing the vocabulary the
        // catalogue already has is better than adding a word for the same thing.
        ("vaccine", "Vial", false),
        // "soap" is a bar, and "mouth wash"/"vaginal wash" are millilitres — so the more specific token has
        // to be tested first or every wash would come out as a bar of soap.
        ("soap", "Bar", false),
        ("spray", "Spray", false),
        ("enema", "Enema", false),
        ("suppositor", "Suppository", true),
        ("pessar", "Pessary", true),
        ("lozenge", "Lozenge", true),
        ("gummy", "Gummy", true),
        ("gummies", "Gummy", true),
        // A herbal "bag" is a tea bag, which is a sachet. Same reasoning as the vaccine above.
        ("herbal bag", "Sachet", true),
        ("sachet", "Sachet", true),
        ("capsule", "Capsule", true),
        ("caps", "Capsule", true),
        ("tablet", "Tablet", true),
        ("tab", "Tablet", true),
        ("drop", "Drop", true),
        ("syrup", "ML", true),
        ("suspension", "ML", true),
        ("solution", "ML", true),
        ("emulsion", "ML", true),
        ("elixir", "ML", true),
        ("lotion", "ML", true),
        ("shampoo", "ML", true),
        ("liquid", "ML", true),
        ("wash", "ML", true),
        ("serum", "ML", true),
        ("scrub", "ML", true),
        ("foam", "ML", true),
        ("roll-on", "ML", true),
        ("oil", "ML", true),
        ("cream", "Gram", true),
        ("ointment", "Gram", true),
        ("gel", "Gram", true),
        ("balm", "Gram", true),
        ("paste", "Gram", true),
        ("granule", "Gram", true),
        ("powder", "Gram", true),
        ("formula", "Gram", true),
    ];

    /// <summary>What this dosage form implies, or nulls when it implies nothing.</summary>
    /// <remarks>
    /// Forms naming a ROUTE or a shape rather than a countable unit — "topical", "vaginal", "device",
    /// "mask", "sheet" — deliberately match nothing. A unit invented for them would appear beside the dose
    /// field and read as data.
    /// </remarks>
    public static DerivedPackFacts FromDosageForm(string? dosageForm)
    {
        if (string.IsNullOrWhiteSpace(dosageForm)) return new DerivedPackFacts(null, null);

        var form = dosageForm.Trim().ToLowerInvariant();
        foreach (var (fragment, unit, splittable) in Forms)
        {
            if (form.Contains(fragment, StringComparison.Ordinal))
                return new DerivedPackFacts(unit, splittable, null, CountableUnits.Contains(unit));
        }

        // Unrecognised. NOT defaulted — see the class remarks.
        return new DerivedPackFacts(null, null);
    }

    /// <summary>
    /// The pack size and splittability implied by the catalogue's two unit columns.
    /// </summary>
    /// <param name="majorUnits">"Major Units (per box)" — strips, blisters or containers per box.</param>
    /// <param name="minorUnits">"Minor Units (total)" — the total PRESCRIBING units in the box.</param>
    /// <remarks>
    /// <para><b>The rule, in one sentence.</b> A pack holding more than one prescribing unit can be split; a
    /// pack that IS one unit cannot. A 120 ml syrup bottle, a 100 gm tube and a single inhaler are all
    /// <c>minor = 1</c> and are all dispensed whole; 20 tablets, 10 sachets and a box of 3 ampoules are all
    /// <c>minor &gt; 1</c> and any number of them may be given.</para>
    ///
    /// <para><b>An incoherent pair derives nothing.</b> A box cannot hold fewer prescribing units than the
    /// containers it is made of, so on the 46 rows where it does, one of the two numbers is wrong and there
    /// is no way to tell which. Taking either would produce a confident quantity out of data known to be
    /// broken.</para>
    /// </remarks>
    public static DerivedPackFacts FromPackUnits(decimal? majorUnits, decimal? minorUnits)
    {
        if (minorUnits is not > 0) return new DerivedPackFacts(null, null);
        if (majorUnits is > 0 && minorUnits < majorUnits) return new DerivedPackFacts(null, null);

        return new DerivedPackFacts(null, minorUnits > 1, minorUnits);
    }

    /// <summary>
    /// Every source, resolved in order of authority.
    /// </summary>
    /// <remarks>
    /// Most authoritative first: the product's OWN record, then the measured pack columns, then the dosage
    /// form. The product-level override always wins — a chewable tablet that must not be halved and a scored
    /// one that may be are both "tablet", and only the product knows which it is.
    /// </remarks>
    public static DerivedPackFacts Resolve(
        string? form,
        bool? statedSplittable,
        string? statedUnit = null,
        decimal? majorUnits = null,
        decimal? minorUnits = null,
        decimal? statedPackSize = null)
    {
        var fromForm = FromDosageForm(form);
        var fromPack = FromPackUnits(majorUnits, minorUnits);

        var unit = statedUnit ?? fromForm.PrescribingUnit;
        return new DerivedPackFacts(
            unit,
            statedSplittable ?? fromPack.IsPackSplittable ?? fromForm.IsPackSplittable,
            statedPackSize ?? fromPack.PackSize,
            // Asked of the RESOLVED unit, so a product-level override of the unit carries the comparison
            // with it rather than leaving the flag describing the unit the form guessed.
            unit is not null && CountableUnits.Contains(unit));
    }

    /// <summary>
    /// Whether all three facts a quantity calculation needs are known.
    /// </summary>
    /// <remarks>
    /// ALL three, not any: a row with a unit and a splittability but no pack size still cannot be converted
    /// into whole packs, and reporting it as complete would produce exactly the confident wrong number
    /// invariant 8 forbids.
    /// </remarks>
    /// <summary>Whether a pack size counts the same thing this unit names — see
    /// <see cref="DerivedPackFacts.PackCountsPrescribingUnits"/>.</summary>
    public static bool PackCounts(string? prescribingUnit) =>
        prescribingUnit is not null && CountableUnits.Contains(prescribingUnit);

    public static bool IsComplete(string? unit, decimal? packSize, bool? splittable) =>
        !string.IsNullOrWhiteSpace(unit) && packSize is > 0 && splittable is not null;
}
