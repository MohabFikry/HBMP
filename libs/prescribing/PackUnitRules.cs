namespace Mersal.Prescribing;

/// <summary>What a product's pack data implies. Nulls mean "not derivable", never a default.</summary>
/// <param name="PackSize">
/// The catalogue's "Minor Units (total)" — what it says is in the box, in ITS units. Kept because the
/// price-per-unit comparison is defined against it, and because it is a recorded fact rather than a derived
/// one.
/// </param>
/// <param name="PackContent">
/// 31.3 — how many PRESCRIBING UNITS one box holds. The divisor for every quantity question.
///
/// <para>For a countable form it is the same number as <paramref name="PackSize"/>: a box of 24 tablets
/// holds 24 tablets. For a measured one the two are different things, and using the pack size was the
/// defect — a 120 ml bottle of syrup is <c>minor = 1</c>, so a 210 ml course divided to <b>210 bottles</b>,
/// and a box of five insulin pens dosed in IU could not be divided at all.</para>
///
/// <para>NULL where the workbook records no volume, weight or concentration to derive it from. The usual
/// fill of an insulin pen is three millilitres and that is not assumed here: a guessed pack size produces a
/// guessed box count, which is a dispensing error indistinguishable from a correct answer (invariant 8).</para>
/// </param>
public sealed record DerivedPackFacts(
    string? PrescribingUnit, bool? IsPackSplittable, decimal? PackSize = null,
    decimal? PackContent = null);

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
    /// <summary>Countable units whose count is the catalogue's MINOR column: 24 tablets in two strips.</summary>
    /// <remarks>
    /// A strip is not a dispensable thing, so the major column here is packaging trivia and the minor column
    /// is the answer. The two disagree on 12,154 of 12,479 tablet rows, and that disagreement is expected.
    /// </remarks>
    private static readonly HashSet<string> ItemUnits = new(StringComparer.Ordinal)
    {
        "Tablet", "Capsule", "Sachet", "Suppository", "Pessary", "Lozenge", "Gummy",
        "Patch", "Dressing", "Enema", "Bar",
    };

    /// <summary>Countable units that ARE the container: vials, ampoules, cartridges, pre-filled syringes.</summary>
    /// <remarks>
    /// Here the two columns are supposed to say the same thing, and on 2,237 of 2,343 rows they do. On the
    /// other 106 they contradict each other in both directions — "adwiflam 75mg/3ml 6 amp" carries
    /// <c>6 / 60</c> while "alejon hair 15 vials x 3 ml" carries <c>1 / 15</c> — so there is no rule that
    /// picks the right one, and a coin-toss between two container counts is a coin-toss between two
    /// quantities of medicine. See <see cref="Containers"/>.
    /// </remarks>
    private static readonly HashSet<string> ContainerUnits = new(StringComparer.Ordinal)
    {
        "Vial", "Ampoule", "Syringe", "Cartridge",
    };

    private static readonly HashSet<string> CountableUnits =
        new(ItemUnits.Concat(ContainerUnits), StringComparer.Ordinal);

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
        //
        // A MOUTH sprays in PUFFS and a NOSE sprays in SPRAYS, so these three precede the bare "spray"
        // below — which is the nasal and topical case, and was the word used for all of them.
        ("oral spray", "Puff", false),
        ("sublingual spray", "Puff", false),
        ("mouth spray", "Puff", false),
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
                return new DerivedPackFacts(unit, splittable);
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
    /// <param name="volumeWeight">"Volume / Weight" — the contents of ONE container ("120 ml", "30 gm").</param>
    /// <param name="strength">"Strength" — where a concentration ("100 iu/ml") is recorded, when it is.</param>
    /// <param name="tradeName">
    /// The product name, read ONLY as a fallback source for the volume and the concentration. It is not a
    /// data column and it is not treated as one; but "toujeo solostar 300 i.u./ml 1.5 ml 3 pens" states its
    /// concentration properly while its Strength cell drops the "/ml", and the fact is the same fact.
    /// </param>
    public static DerivedPackFacts Resolve(
        string? form,
        bool? statedSplittable,
        string? statedUnit = null,
        decimal? majorUnits = null,
        decimal? minorUnits = null,
        decimal? statedPackSize = null,
        string? volumeWeight = null,
        string? strength = null,
        string? tradeName = null)
    {
        var fromForm = FromDosageForm(form);
        var fromPack = FromPackUnits(majorUnits, minorUnits);

        /*
         * A CONCENTRATION IN IU PER MILLILITRE MEANS THE MEDICINE IS COUNTED IN IU — whatever holds it.
         *
         * Insulin is the case that says so out loud: it arrives in vials, cartridges and pre-filled pens,
         * and a prescriber writes "25 IU at night" for every one of them. Taking the unit from the CONTAINER
         * put "Cartridge" beside the dose field of a medicine nobody has ever dosed in cartridges.
         *
         * The concentration is what makes this safe to infer. A bare total — "50000 iu" on a vitamin D
         * capsule — is deliberately not read as one: that product IS prescribed in capsules, and there is
         * nothing per-millilitre about it.
         */
        var concentration = PackMeasure.IuPerMillilitre(strength) ?? PackMeasure.IuPerMillilitre(tradeName);

        var unit = statedUnit ?? (concentration is not null ? "IU" : fromForm.PrescribingUnit);
        var packSize = statedPackSize ?? fromPack.PackSize;

        return new DerivedPackFacts(
            unit,
            statedSplittable ?? fromPack.IsPackSplittable ?? fromForm.IsPackSplittable,
            packSize,
            ContentOf(unit, packSize, majorUnits, volumeWeight, concentration, tradeName));
    }

    /// <summary>
    /// How many prescribing units one box holds — see <see cref="DerivedPackFacts.PackContent"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Containers come from the MAJOR column, never the minor one.</b> "Major Units (per box)" is
    /// the container count and behaves like one across the catalogue: 1 for a 10 ml vial, 5 for five
    /// penfills, 3 for three pens, 6 for six ampoules. The minor column is not a container count and does
    /// not claim to be — "actrapid hm 100 i.u./ml 10 ml vial" carries <c>minor = 10</c>, which is its
    /// millilitres, and multiplying a per-container volume by it would give that box 100 ml.</para>
    ///
    /// <para><b>Countable forms skip all of it.</b> A box of 24 tablets holds 24 tablets; there is nothing
    /// to measure, and the minor column is exactly the right answer.</para>
    /// </remarks>
    private static decimal? ContentOf(
        string? unit, decimal? packSize, decimal? majorUnits, string? volumeWeight, decimal? concentration,
        string? tradeName)
    {
        if (unit is null) return null;
        if (ItemUnits.Contains(unit)) return packSize is > 0 ? packSize : null;
        if (ContainerUnits.Contains(unit)) return Containers(majorUnits, packSize);

        /*
         * A MEASURED product takes its container count from the major column alone.
         *
         * Here the two columns are not expected to agree, and their disagreement is not evidence of an
         * error: "actrapid hm 100 i.u./ml 10 ml vial" carries major = 1 and minor = 10 because the minor
         * column is counting the vial's MILLILITRES. Demanding agreement — which is right for a box of
         * ampoules, where both columns claim to count ampoules — would discard a row whose contents are
         * exactly derivable.
         *
         * An absent major column derives nothing rather than falling back to minor, because on these rows
         * minor is the number that is most likely to be measuring something else.
         */
        if (majorUnits is not > 0) return null;
        var containers = majorUnits.Value;

        // The volume column first; the trade name only where it is empty ("… (10ml) vial").
        var millilitres = PackMeasure.Millilitres(volumeWeight) ?? PackMeasure.Millilitres(tradeName);

        return unit switch
        {
            "ML" => Times(containers, millilitres),
            "Gram" => Times(containers, PackMeasure.Grams(volumeWeight) ?? PackMeasure.Grams(tradeName)),
            "IU" => concentration is { } iu ? Times(containers * iu, millilitres) : null,
            // Puff, Drop and Spray: the catalogue records no count of actuations for any product, so there is
            // nothing to derive from. Withheld rather than approximated from the container's volume, which
            // would need a per-actuation dose nobody has recorded either.
            _ => null,
        };
    }

    /// <summary>
    /// How many containers are in the box, or null when the two columns disagree about it.
    /// </summary>
    /// <remarks>
    /// Same principle as <see cref="FromPackUnits"/>: an incoherent pair derives nothing. Where the columns
    /// agree — 95.5% of container rows — either is the answer. Where they do not, one of them is wrong, there
    /// is no way to tell which, and the difference is the difference between one box and ten.
    /// </remarks>
    private static decimal? Containers(decimal? majorUnits, decimal? minorUnits)
    {
        if (majorUnits is not > 0) return minorUnits is > 0 ? minorUnits : null;
        if (minorUnits is > 0 && minorUnits != majorUnits) return null;
        return majorUnits;
    }

    private static decimal? Times(decimal a, decimal? b) => b is { } v && v > 0 ? a * v : null;

    /// <summary>Every unit the platform reasons with — the closed vocabulary the drug table's CHECK holds.</summary>
    /// <remarks>
    /// Declared here rather than in the migration alone so that a unit cannot enter the vocabulary without
    /// also acquiring a short form; <c>PackContentTests</c> asserts the pair.
    /// </remarks>
    public static IReadOnlyCollection<string> Units { get; } =
        [.. CountableUnits, "ML", "Gram", "Puff", "IU", "Drop", "Spray"];

    /// <summary>
    /// 31.3 — the unit as a prescriber writes it: <c>tabs</c>, <c>caps</c>, <c>IU</c>, <c>puffs</c>.
    /// </summary>
    /// <remarks>
    /// The dose field is labelled with this. The stored vocabulary is singular and title-cased because it is
    /// a database value — "Tablet", "Capsule", "Ampoule" — and a field labelled "Dose (Tablet)" reads as a
    /// column name leaking onto a prescription. Unknown values are returned unchanged rather than blanked:
    /// showing the raw word is worse than showing the short one and far better than showing nothing.
    /// </remarks>
    public static string ShortUnit(string? unit) => unit switch
    {
        null or "" => "",
        "Tablet" => "tabs",
        "Capsule" => "caps",
        "Sachet" => "sachets",
        "Suppository" => "supps",
        "Pessary" => "pessaries",
        "Lozenge" => "lozenges",
        "Gummy" => "gummies",
        "Vial" => "vials",
        "Ampoule" => "amps",
        "Syringe" => "syringes",
        "Cartridge" => "cartridges",
        "Patch" => "patches",
        "Dressing" => "dressings",
        "Enema" => "enemas",
        "Bar" => "bars",
        "ML" => "ml",
        "Gram" => "gm",
        "Puff" => "puffs",
        "Drop" => "drops",
        "Spray" => "sprays",
        "IU" => "IU",
        _ => unit,
    };

    /// <summary>
    /// Whether every fact a quantity calculation needs is known.
    /// </summary>
    /// <remarks>
    /// ALL of them, not any: a row with a unit and a splittability but no pack CONTENT cannot be converted
    /// into boxes, and reporting it as complete would produce exactly the confident wrong number invariant 8
    /// forbids. The content — not the pack size — is the one that matters, which is the whole of 31.3: a
    /// syrup with <c>pack_size = 1</c> looked complete and divided a 210 ml course into 210 bottles.
    /// </remarks>
    public static bool IsComplete(string? unit, decimal? packContent, bool? splittable) =>
        !string.IsNullOrWhiteSpace(unit) && packContent is > 0 && splittable is not null;
}
