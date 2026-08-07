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

    /// <summary>Relevance bucket: 0 trade-name prefix, 1 Arabic-name prefix, 2 ingredient prefix, 3 contains.</summary>
    public int Rank { get; set; }
}
