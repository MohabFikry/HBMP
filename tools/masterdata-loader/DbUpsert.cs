using Mersal.MasterData.Domain;
using Mersal.MasterData.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.MasterData.Loader;

/// <summary>
/// Idempotent upsert by natural key: existing rows are updated in place, new rows inserted, so a second
/// run produces identical counts (no duplicates). ATC classes load before drugs so FKs resolve.
/// </summary>
public static class DbUpsert
{
    /// <summary>
    /// Applies every migration in order, as LITERAL SQL.
    /// </summary>
    /// <remarks>
    /// Deliberately a raw <see cref="System.Data.Common.DbCommand"/> rather than
    /// <c>ExecuteSqlRawAsync</c>. That overload runs the script through <c>String.Format</c> before sending
    /// it, so any brace in the SQL is read as a placeholder — and PostgreSQL uses braces for array literals
    /// (<c>DEFAULT '{}'</c>) and for dollar-quoted function bodies. A migration is not a format string, and
    /// the failure it produces is a FormatException pointing at a character offset rather than at the
    /// statement, which tells a reader nothing about which migration broke.
    /// </remarks>
    public static async Task ApplyMigrationAsync(MasterDataDbContext db, string migrationsDir, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);

        foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = await File.ReadAllTextAsync(file, ct);
            // Migrations build indexes over the whole catalogue; the default 30s is not enough for a cold run.
            command.CommandTimeout = 600;

            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                // Name the file. A migration runner that fails anonymously across 13 scripts is one somebody
                // debugs by bisecting the directory.
                throw new InvalidOperationException(
                    $"migration failed: {Path.GetFileName(file)} — {ex.Message}", ex);
            }
        }
    }

    public static async Task UpsertIcdAsync(MasterDataDbContext db, IReadOnlyList<IcdCode> items, LoadReport r, CancellationToken ct)
    {
        var existing = (await db.IcdCodes.Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var i in items)
        {
            if (existing.Contains(i.Code)) { db.IcdCodes.Update(i); r.Updated++; }
            else { db.IcdCodes.Add(i); r.Inserted++; }
        }
        await db.SaveChangesAsync(ct);
        r.FinalCount = await db.IcdCodes.CountAsync(ct);
    }

    public static async Task UpsertCptAsync(MasterDataDbContext db, IReadOnlyList<CptCode> items, LoadReport r, CancellationToken ct)
    {
        var existing = (await db.CptCodes.Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var i in items)
        {
            if (existing.Contains(i.Code)) { db.CptCodes.Update(i); r.Updated++; }
            else { db.CptCodes.Add(i); r.Inserted++; }
        }
        await db.SaveChangesAsync(ct);
        r.FinalCount = await db.CptCodes.CountAsync(ct);
    }

    public static async Task UpsertAtcAsync(MasterDataDbContext db, IReadOnlyList<AtcClass> items, LoadReport r, CancellationToken ct)
    {
        var existing = (await db.AtcClasses.Select(x => x.AtcCode).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var i in items.OrderBy(x => x.Level)) // parents before children
        {
            if (existing.Contains(i.AtcCode)) { db.AtcClasses.Update(i); r.Updated++; }
            else { db.AtcClasses.Add(i); r.Inserted++; }
        }
        await db.SaveChangesAsync(ct);
        r.FinalCount = await db.AtcClasses.CountAsync(ct);
    }

    /// <summary>
    /// Upserts drugs, preserving the surrogate id of any row that already exists.
    /// </summary>
    /// <remarks>
    /// Matching runs source_row_id first, then drug_code. The fallback is what lets the workbook adopt rows
    /// loaded from the earlier CSV (which had no id column) instead of inserting duplicates beside them —
    /// and adopting means keeping the <b>existing</b> uuid, because prescriptions, interactions and
    /// indications already point at it. Rows present in the database but absent from this file are left
    /// alone: reference data is never hard-deleted.
    /// </remarks>
    /// <summary>
    /// 29.7 — recompute every drug's lowest-price label (design 45 §7).
    ///
    /// <para>Run after the drugs are upserted, because it reads the prices that upsert just wrote. It lives
    /// HERE and not behind an endpoint: masterdata is a read-only reference catalogue with no
    /// <c>masterdata:write</c> scope, and §7 says the labels are "recomputed whenever prices LOAD".</para>
    ///
    /// <para><c>computed_at</c> is stamped on EVERY row, not only the winners — it answers "was this row
    /// considered?", and stamping only the labelled ones would make an uncomparable drug indistinguishable
    /// from one the recompute never reached.</para>
    /// </summary>
    public static async Task RecomputeLowestPriceAsync(
        MasterDataDbContext db, DateTimeOffset now, LoadReport report, CancellationToken ct)
    {
        var drugs = await db.Drugs.ToListAsync(ct);
        var labels = LowestPrice.Compute(drugs.ConvertAll(d =>
            new PricedDrug(d.DrugId.ToString(), d.ScientificName, d.Strength, d.Form, d.PriceEgp, d.PackSize)));
        var byId = labels.ToDictionary(l => l.DrugId, StringComparer.Ordinal);

        foreach (var drug in drugs)
        {
            if (!byId.TryGetValue(drug.DrugId.ToString(), out var label)) continue;
            drug.IsLowestPrice = label.IsLowestPrice;
            drug.PricePerUnit = label.PricePerUnit;
            drug.LowestPriceGroupKey = label.GroupKey;
            drug.LowestPriceComputedAt = now;
        }

        await db.SaveChangesAsync(ct);

        var notComparable = labels.Count(l => l.PricePerUnit is null);
        report.Note(
            $"lowest-price: {labels.Count(l => l.IsLowestPrice):N0} labelled across "
            + $"{labels.Count(l => l.GroupKey is not null):N0} grouped rows; {notComparable:N0} NOT comparable "
            + "(no price or no pack size). A drug with no pack size is never labelled — falling back to PACK "
            + "price is the exact comparison design 45 §7 exists to prevent.");
    }

    public static async Task UpsertDrugsAsync(MasterDataDbContext db, IReadOnlyList<Drug> items, LoadReport r, CancellationToken ct)
    {
        var existing = await db.Drugs
            .Select(x => new { x.DrugId, x.DrugCode, x.SourceRowId })
            .ToListAsync(ct);

        var bySourceRowId = existing
            .Where(x => x.SourceRowId is not null)
            .ToDictionary(x => x.SourceRowId!, x => x.DrugId, StringComparer.Ordinal);
        var byDrugCode = existing
            .GroupBy(x => x.DrugCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DrugId, StringComparer.Ordinal);

        foreach (var i in items)
        {
            Guid? found = i.SourceRowId is not null && bySourceRowId.TryGetValue(i.SourceRowId, out var bySource)
                ? bySource
                : byDrugCode.TryGetValue(i.DrugCode, out var byCode) ? byCode : null;

            if (found is { } id) { i.DrugId = id; db.Drugs.Update(i); r.Updated++; }
            else { db.Drugs.Add(i); r.Inserted++; }
        }
        await db.SaveChangesAsync(ct);
        r.FinalCount = await db.Drugs.CountAsync(ct);
    }

    /// <summary>
    /// Rebuilds the ICD-10 ancestor closure from whatever <c>parent_code</c> now says.
    /// </summary>
    /// <remarks>
    /// Runs after the ICD upsert, because the closure is derived from the rows that upsert just wrote. The
    /// function itself lives in migration 0011 so the recursive SQL exists in exactly one place; this only
    /// decides WHEN it runs. Without it the parent chain is loaded and the descendant-or-self lookup the
    /// indication check makes finds nothing — the hierarchy would be present and unusable.
    /// </remarks>
    public static async Task RebuildIcdAncestorsAsync(MasterDataDbContext db, CancellationToken ct)
        => await db.Database.ExecuteSqlRawAsync("SELECT masterdata.rebuild_icd_ancestors();", ct);

    /// <summary>
    /// Upserts derived ingredients, never overwriting a CURATED row.
    /// </summary>
    /// <remarks>
    /// The curated rows seeded by migrations 0009/0010/0012/0013 carry an Arabic name, a substance-level ATC
    /// code and a named source; the derived rows carry a key and an English name and nothing else. Updating
    /// a curated row from a derived one would silently strip the Arabic name off every molecule a product
    /// happens to mention — so an existing key is left exactly as it is.
    /// </remarks>
    public static async Task UpsertIngredientsAsync(
        MasterDataDbContext db, IReadOnlyList<Ingredient> items, LoadReport r, CancellationToken ct)
    {
        var existing = (await db.Set<Ingredient>().Select(x => x.IngredientKey).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var fresh = items
            .Where(i => !existing.Contains(i.IngredientKey))
            .GroupBy(i => i.IngredientKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        r.Read = items.Count;
        r.Inserted = fresh.Count;
        r.Updated = 0;

        db.Set<Ingredient>().AddRange(fresh);
        await db.SaveChangesAsync(ct);
        r.FinalCount = await db.Set<Ingredient>().CountAsync(ct);
    }

    /// <summary>
    /// Upserts the product → molecule links.
    /// </summary>
    /// <remarks>
    /// Runs after the drug upsert so every <c>drug_id</c> resolves — including the rows whose uuid was
    /// ADOPTED from an earlier load rather than derived, which is why the caller passes the drug list the
    /// upsert has already mutated rather than rebuilding it from the file.
    /// </remarks>
    public static async Task UpsertDrugIngredientsAsync(
        MasterDataDbContext db, IReadOnlyList<DrugIngredient> items, LoadReport r, CancellationToken ct)
    {
        var drugIds = items.Select(i => i.DrugId).ToHashSet();
        var existing = await db.Set<DrugIngredient>()
            .Where(x => drugIds.Contains(x.DrugId))
            .ToListAsync(ct);

        var loaded = items.Select(i => (i.DrugId, i.IngredientKey)).ToHashSet();

        // AUTHORITATIVE for the drugs in this load. A link the source no longer supports is removed, not
        // left behind — a reformulated product keeping a molecule it no longer contains would produce a
        // confident allergy or interaction warning about an ingredient that is not in the box. Drugs absent
        // from this load are untouched.
        var stale = existing.Where(x => !loaded.Contains((x.DrugId, x.IngredientKey))).ToList();
        db.Set<DrugIngredient>().RemoveRange(stale);

        var present = existing.Select(x => (x.DrugId, x.IngredientKey)).ToHashSet();
        var fresh = items
            .Where(i => !present.Contains((i.DrugId, i.IngredientKey)))
            .GroupBy(i => (i.DrugId, i.IngredientKey))
            .Select(g => g.First())
            .ToList();

        r.Read = items.Count;
        r.Inserted = fresh.Count;
        r.Updated = 0;
        if (stale.Count > 0) r.Note($"{stale.Count} stale product→molecule link(s) removed");

        db.Set<DrugIngredient>().AddRange(fresh);
        await db.SaveChangesAsync(ct);
        r.FinalCount = await db.Set<DrugIngredient>().CountAsync(ct);
    }

    /// <summary>
    /// Upserts drug indications, keyed on (drug_id, icd_code) so a re-load updates in place.
    /// </summary>
    /// <remarks>
    /// Indications for a drug present in this load are replaced wholesale — a code the source has dropped is
    /// soft-deleted rather than removed, so "this drug used to be indicated for X" survives in the record.
    /// Drugs absent from the load are untouched.
    /// </remarks>
    public static async Task UpsertDrugIndicationsAsync(
        MasterDataDbContext db, IReadOnlyList<DrugIndication> items, LoadReport r, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var drugIds = items.Select(i => i.DrugId).ToHashSet();

        var existing = await db.DrugIndications
            .Where(x => drugIds.Contains(x.DrugId))
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(x => (x.DrugId, x.IcdCode));

        var loaded = new HashSet<(Guid, string)>();
        foreach (var i in items)
        {
            loaded.Add((i.DrugId, i.IcdCode));
            if (byKey.TryGetValue((i.DrugId, i.IcdCode), out var row))
            {
                row.Source = i.Source;
                row.SourceRelease = i.SourceRelease;
                row.IsPrimary = i.IsPrimary;
                row.DeletedAt = null;         // a code that came back is live again
                row.UpdatedAt = now;
                r.Updated++;
            }
            else
            {
                i.CreatedAt = now;
                i.UpdatedAt = now;
                db.DrugIndications.Add(i);
                r.Inserted++;
            }
        }

        var withdrawn = 0;
        foreach (var row in existing)
        {
            if (row.DeletedAt is null && !loaded.Contains((row.DrugId, row.IcdCode)))
            {
                row.DeletedAt = now;
                row.UpdatedAt = now;
                withdrawn++;
            }
        }
        if (withdrawn > 0) r.Note($"{withdrawn} indication(s) withdrawn by this release (soft-deleted, not removed).");

        await db.SaveChangesAsync(ct);
        r.FinalCount = await db.DrugIndications.CountAsync(x => x.DeletedAt == null, ct);
    }
}
