using Microsoft.EntityFrameworkCore;

namespace Mersal.Audit.Infrastructure;

/// <summary>
/// Applies the hand-authored SQL migrations (partitioning + INSERT-only grants + RLS need raw SQL
/// that EF migrations can't express cleanly). Runs the numbered scripts in Migrations/ in order.
/// Each script is idempotent (IF NOT EXISTS / OR REPLACE), so re-running is safe.
/// </summary>
public static class SqlFileMigrator
{
    public static async Task ApplyAsync(AuditDbContext db, string migrationsDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(migrationsDir)) return;

        foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var sql = await File.ReadAllTextAsync(file, ct);
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }
}
