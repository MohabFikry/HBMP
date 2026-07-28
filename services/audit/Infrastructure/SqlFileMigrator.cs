using Microsoft.EntityFrameworkCore;

namespace Mersal.Audit.Infrastructure;

/// <summary>
/// DEAD CODE — no caller, and do not give it one. Kept only because deleting it needs a separate
/// commit; the next change to this file should be its removal.
///
/// Applies the hand-authored SQL migrations (partitioning + INSERT-only grants + RLS need raw SQL
/// that EF migrations can't express cleanly). Runs the numbered scripts in Migrations/ in order.
/// Each script is idempotent (IF NOT EXISTS / OR REPLACE), so re-running is safe.
///
/// WHY IT IS UNWIRED: audit-service connects as <c>hbmp_audit</c>, which 18.B2 deliberately made a
/// non-owner of schema <c>audit</c> (audit_event is FORCE ROW LEVEL SECURITY, and 0002's REVOKE of
/// UPDATE/DELETE only holds while the writer cannot re-grant itself). Running 0001 under that role
/// fails on <c>CREATE SCHEMA IF NOT EXISTS audit</c> — Postgres performs the CREATE-on-database ACL
/// check even when the schema exists — so the service crash-looped on <c>42501</c> every boot.
/// "Idempotent" is not the same as "runnable by a least-privilege role".
///
/// Migrations are applied out of band by <c>tools/ci/apply-migrations.sh</c> under an owning role,
/// which is what CI does and what every other service already relied on.
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
