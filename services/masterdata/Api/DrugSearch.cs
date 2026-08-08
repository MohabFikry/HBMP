using Mersal.Prescribing;
using Mersal.MasterData.Domain;
using Mersal.MasterData.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.MasterData.Api;

/// <summary>
/// One option in the prescribing combobox (phase 26.2, doc 43 §6).
/// </summary>
/// <param name="DrugId">
/// The REAL uuid. The prescribe modal used to send the ATC code string where the API expects a Guid
/// (HttpApiClient.ts:883), so the path could not work against real data at all; carrying the uuid from the
/// moment of selection is the fix, and there is a regression test for it.
/// </param>
/// <param name="ActiveIngredient">
/// Rendered under the trade name, and a safety feature rather than decoration: two boxes with different
/// trade names holding the same molecule is the commonest prescribing duplication, and showing the
/// ingredient at the moment of choosing is the cheapest defence against it.
/// </param>
/// <param name="HasIndicationData">
/// Whether the drug has any indication rows. Lets the UI distinguish "this diagnosis is not a listed
/// indication" from "nothing is recorded for this drug" — 1,019 products are in the second case.
/// </param>
public sealed record DrugSearchHit(
    Guid DrugId,
    string TradeName,
    string? TradeNameAr,
    string? ActiveIngredient,
    string? Strength,
    string? Form,
    decimal? PriceEgp,
    string? AtcCode,
    bool HasIndicationData,
    // 29.7 (design 45 §7) — DERIVED by the loader, rendered by the combobox. These reached the database and
    // stopped there: the columns were populated on every load and this projection never selected them, so
    // the chip could not render however correct the computation was.
    bool IsLowestPrice,
    decimal? PricePerUnit,
    string Availability,
    // 29.6 — the pack facts, so the composer can label the dose field and say how much will be dispensed
    // without a second call. Every one of them is nullable, and a null renders as an absence.
    string? PrescribingUnit,
    decimal? PackSize,
    bool? IsPackSplittable)
{
    /// <summary>
    /// 31.3 — the unit as a prescriber writes it: <c>tabs</c>, <c>caps</c>, <c>IU</c>, <c>puffs</c>.
    /// </summary>
    /// <remarks>
    /// Derived here rather than in the browser, from the same table that owns the vocabulary. The stored
    /// words are database values — "Tablet", "Capsule", "Ampoule" — and a dose field labelled "Dose
    /// (Tablet)" reads as a column name that escaped onto a prescription.
    /// </remarks>
    public string PrescribingUnitShort => PackUnitRules.ShortUnit(PrescribingUnit);
}

public static class DrugSearch
{
    /// <summary>
    /// Below this a typeahead matches most of the catalogue; the UI does not send it either.
    /// </summary>
    /// <remarks>
    /// Measured against the full 31,651-row catalogue: a 2-character query costs ~75 ms and a 3-or-more
    /// character one 5–17 ms. The step is not noise — pg_trgm indexes trigrams, so a 2-character term has no
    /// trigram to look up and the planner falls back to a scan. Both are inside the 300 ms budget, so the
    /// minimum stays at 2 where the UX wants it; raising it to 3 is the lever if that budget ever tightens.
    /// </remarks>
    public const int MinQueryLength = 2;

    /// <summary>A combobox shows a handful of options; 50 is already more than is useful.</summary>
    public const int MaxPageSize = 50;

    /// <summary>
    /// Searches trade name, active ingredient and Arabic name in one field, ranked by how directly the row
    /// answers the query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raw SQL rather than LINQ for one reason: the query must call <c>masterdata.search_key</c> exactly as
    /// the GIN indexes declare it. Normalising the query differently from the indexed expression leaves the
    /// index unused and turns every keystroke into a scan of 31,651 rows — a failure that is silent, because
    /// the results stay correct and only the latency changes. Written here so the match is visible.
    /// </para>
    /// <para>
    /// Scoped to the CURRENT market list (<c>source_row_id IS NOT NULL</c>). The catalogue also holds 8,998
    /// rows from the superseded CSV load, which carry no indication data; they stay reachable by id and by
    /// code so historical prescriptions resolve, but offering a prescriber two entries for one product —
    /// where only one can be checked against a diagnosis — would be a safety regression dressed as
    /// completeness.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<DrugSearchHit>> SearchAsync(
        MasterDataDbContext db, string query, int page, int pageSize, CancellationToken ct)
    {
        var key = query.Trim();
        var offset = (page - 1) * pageSize;

        var rows = await db.Set<DrugSearchRow>().FromSql($"""
            WITH q AS (SELECT masterdata.search_key({key}) AS k)
            SELECT  d.drug_id,
                    d.name,
                    d.name_ar,
                    d.scientific_name,
                    d.strength,
                    d.form,
                    d.price_egp,
                    d.atc_code,
                    EXISTS (
                        SELECT 1 FROM masterdata.drug_indication di
                        WHERE di.drug_id = d.drug_id AND di.deleted_at IS NULL
                    ) AS has_indication_data,
                    d.is_lowest_price,
                    d.price_per_unit,
                    d.availability,
                    d.prescribing_unit,
                    d.pack_size,
                    d.is_pack_splittable,
                    CASE
                        WHEN masterdata.search_key(d.name)            LIKE q.k || '%' THEN 0
                        WHEN masterdata.search_key(d.name_ar)         LIKE q.k || '%' THEN 1
                        WHEN masterdata.search_key(d.scientific_name) LIKE q.k || '%' THEN 2
                        ELSE 3
                    END AS rank
            FROM masterdata.drug d, q
            WHERE d.source_row_id IS NOT NULL
              AND (   masterdata.search_key(d.name)            LIKE '%' || q.k || '%'
                   OR masterdata.search_key(d.scientific_name) LIKE '%' || q.k || '%'
                   OR masterdata.search_key(d.name_ar)         LIKE '%' || q.k || '%')
            ORDER BY rank, d.price_egp NULLS LAST, d.name
            OFFSET {offset} LIMIT {pageSize}
            """).AsNoTracking().ToListAsync(ct);

        return [.. rows.Select(r => new DrugSearchHit(
            r.DrugId, r.Name, r.NameAr, r.ScientificName,
            r.Strength, r.Form, r.PriceEgp, r.AtcCode, r.HasIndicationData,
            r.IsLowestPrice, r.PricePerUnit, r.Availability,
            r.PrescribingUnit, r.PackSize, r.IsPackSplittable))];
    }
}
