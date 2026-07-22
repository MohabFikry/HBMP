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
    public static async Task ApplyMigrationAsync(MasterDataDbContext db, string migrationsDir, CancellationToken ct)
    {
        foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(file, ct), ct);
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

    public static async Task UpsertDrugsAsync(MasterDataDbContext db, IReadOnlyList<Drug> items, LoadReport r, CancellationToken ct)
    {
        // Match on the natural key (drug_code); preserve existing surrogate ids on update.
        var existing = await db.Drugs.ToDictionaryAsync(x => x.DrugCode, x => x.DrugId, ct);
        foreach (var i in items)
        {
            if (existing.TryGetValue(i.DrugCode, out var id)) { i.DrugId = id; db.Drugs.Update(i); r.Updated++; }
            else { db.Drugs.Add(i); r.Inserted++; }
        }
        await db.SaveChangesAsync(ct);
        r.FinalCount = await db.Drugs.CountAsync(ct);
    }
}
