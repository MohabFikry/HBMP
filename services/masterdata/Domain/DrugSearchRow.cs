namespace Mersal.MasterData.Domain;

/// <summary>
/// One row of the drug typeahead result (phase 26.2). A keyless query type — it is the shape a raw SQL
/// search returns, not a table.
/// </summary>
/// <remarks>
/// It lives in Domain rather than beside the endpoint because the DbContext has to register it, and
/// Infrastructure cannot reference Api.
/// </remarks>
public sealed class DrugSearchRow
{
    public Guid DrugId { get; set; }
    public string Name { get; set; } = default!;
    public string? NameAr { get; set; }
    public string? ScientificName { get; set; }
    public string? Strength { get; set; }
    public string? Form { get; set; }
    public decimal? PriceEgp { get; set; }
    public string? AtcCode { get; set; }

    /// <summary>
    /// Whether the drug has any indication rows at all. Lets the caller tell "this diagnosis is not a listed
    /// indication" from "nothing is recorded for this drug" — 1,019 products are in the second case, and
    /// collapsing the two would present an unchecked drug as a checked one.
    /// </summary>
    public bool HasIndicationData { get; set; }

    // ---- 29.7 (design 45 §7) ------------------------------------------------------------------------

    /// <summary>
    /// Cheapest per PRESCRIBING UNIT within ingredient + strength + form. DERIVED — recomputed on every
    /// price load, never authored — so this query reads it rather than deciding it.
    /// </summary>
    public bool IsLowestPrice { get; set; }

    /// <summary>
    /// price ÷ pack size. Null where the pack size is unknown, and such a drug is never labelled: comparing
    /// PACK prices is the error §7 exists to prevent, because a 20-tab pack at 100 EGP is dearer per tablet
    /// than a 30-tab pack at 120.
    /// </summary>
    public decimal? PricePerUnit { get; set; }

    /// <summary>
    /// Available / Unavailable / <b>Unknown</b> — three states, not a boolean, and Unknown is the
    /// catalogue-wide default that renders NOTHING.
    /// </summary>
    public string Availability { get; set; } = "Unknown";

    // ---- 29.6 (design 45 §6) — the pack facts the COMPOSER needs -------------------------------------

    /// <summary>
    /// What the dose and quantity are counted in — Tablet, ML, Puff.
    /// </summary>
    /// <remarks>
    /// Carried on the SEARCH row, not fetched afterwards, because the composer puts it beside the dose field
    /// the moment a drug is chosen. A second round trip to label a field is a field that is briefly unlabelled,
    /// and "60" beside a medicine with no unit is a number the prescriber has to infer from the product name.
    /// NULL is honest — 838 rows have no derivable unit — and renders as no unit rather than a guess.
    /// </remarks>
    public string? PrescribingUnit { get; set; }

    /// <summary>Prescribing units per pack. Null where the catalogue does not record one.</summary>
    public decimal? PackSize { get; set; }

    /// <summary>
    /// Whether fewer than a whole pack may be dispensed. NULL is NOT false: it means the catalogue does not
    /// say, and the quantity check reports NotChecked naming the field rather than rounding to a pack.
    /// </summary>
    public bool? IsPackSplittable { get; set; }

    /// <summary>Relevance bucket: 0 trade-name prefix, 1 Arabic-name prefix, 2 ingredient prefix, 3 contains.</summary>
    public int Rank { get; set; }
}
