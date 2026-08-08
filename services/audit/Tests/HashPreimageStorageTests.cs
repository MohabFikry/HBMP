using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Audit.Tests;

/// <summary>
/// The guard for migration 0003: <b>a column that is part of the hash pre-image must never be declared as a
/// NORMALISING type.</b>
///
/// <para><c>before_state</c> and <c>after_state</c> were <c>jsonb</c>. jsonb is a parsed representation, not
/// a string: Postgres re-renders it on read, inserting a space after every <c>:</c> and sorting object keys.
/// <c>record_hash</c> is computed at ingest over the compact JSON the emitting service wrote, so the verifier
/// recomputed a different hash and reported intact records as tampered — while destroying the key order of 75
/// records permanently.</para>
///
/// <para>This reads the MIGRATIONS rather than a live database, so it fails in every environment including a
/// developer's laptop with no Postgres, and it fails on the change that introduces the defect rather than on
/// the first verifier run afterwards. A schema check that needs a database is one that does not run.</para>
/// </summary>
public class HashPreimageStorageTests
{
    /// <summary>Every column the canonicalizer feeds into the hash that is stored as free text. Extend this
    /// list whenever <c>AuditCanonicalizer</c> gains a field backed by a column.</summary>
    private static readonly string[] PreimageTextColumns = ["before_state", "after_state"];

    /// <summary>Types Postgres is free to re-render. Anything here destroys a hash pre-image.</summary>
    private static readonly string[] NormalisingTypes = ["jsonb", "json", "xml"];

    [Theory]
    [InlineData("before_state")]
    [InlineData("after_state")]
    public void A_hash_preimage_column_is_never_declared_as_a_normalising_type(string column)
    {
        var sql = AllMigrationSql();

        // The column's LAST declaration wins — 0001 created it as jsonb, 0003 alters it to text — so the
        // check is on what the migration set says at the end, not on whether jsonb ever appeared.
        var declaredType = FinalTypeOf(sql, column);

        declaredType.Should().NotBeNull($"'{column}' must be declared somewhere in the audit migrations");
        NormalisingTypes.Should().NotContain(declaredType!,
            $"'{column}' is part of the record_hash pre-image. A normalising type re-renders it on read, so "
            + "the verifier recomputes a different hash and reports intact records as TAMPERED — and jsonb "
            + "additionally discards object key order, which destroys the pre-image for good. See migration "
            + "0003 and docs/audit-chain-integrity-2026-08.md.");
        declaredType.Should().Be("text");
    }

    [Fact]
    public void The_guard_covers_every_preimage_column_the_canonicalizer_reads()
    {
        // Guards the guard. If AuditCanonicalizer gains a field whose column could normalise, this list has to
        // grow with it — otherwise the check passes while the new column has the old defect.
        var canonicalizer = File.ReadAllText(
            Path.Combine(RepoRoot(), "libs/audit-client/AuditCanonicalizer.cs"));

        foreach (var col in PreimageTextColumns)
        {
            var property = string.Concat(col.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
            canonicalizer.Should().Contain($"e.{property}",
                $"'{col}' is guarded as a pre-image column but the canonicalizer no longer reads it — either "
                + "the guard is stale or the hash has quietly stopped covering that field");
        }
    }

    private static string AllMigrationSql() =>
        string.Join('\n', Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "services/audit/Infrastructure/Migrations"), "*.sql")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText));

    /// <summary>The type a column ends up with, reading CREATE TABLE then any ALTER … TYPE in file order.</summary>
    private static string? FinalTypeOf(string sql, string column)
    {
        string? type = null;

        // `    before_state        jsonb,` — the CREATE TABLE declaration.
        var created = Regex.Match(sql, $@"^\s*{Regex.Escape(column)}\s+(?<t>[a-z]+)", RegexOptions.Multiline);
        if (created.Success) type = created.Groups["t"].Value;

        // `ALTER COLUMN before_state TYPE text USING …` — later wins.
        foreach (Match m in Regex.Matches(
                     sql, $@"ALTER\s+COLUMN\s+{Regex.Escape(column)}\s+TYPE\s+(?<t>[a-z]+)", RegexOptions.IgnoreCase))
        {
            type = m.Groups["t"].Value.ToLowerInvariant();
        }

        return type;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
