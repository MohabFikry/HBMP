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

        // Execute via a raw ADO command rather than EF's ExecuteSqlRaw: EF runs the SQL through
        // String.Format to bind {n} placeholders, so any literal brace in the DDL (e.g. a
        // `text[] DEFAULT '{}'` array default) throws a FormatException. A raw command runs the
        // script verbatim.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var sql = await File.ReadAllTextAsync(file, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
