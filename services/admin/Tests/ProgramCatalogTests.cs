using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Admin.Api;

namespace Mersal.Admin.Tests;

/// <summary>
/// 21.6 — the programme catalog the administration screen renders must match the database's own list.
///
/// <see cref="ProgramCatalog"/> exists so the screen can show EVERY switch, including the ones a tenant has
/// no row for — otherwise the screen can only edit programmes somebody already configured, which is the one
/// job it has. That means the list is duplicated: once in migration 0008's CHECK constraint, once in C#.
///
/// A hand-copied list drifts silently and fails late: a feature added to the migration but not here is
/// invisible in the UI (so nobody can enable it), and one added here but not there is a switch that renders,
/// accepts a click, and 500s on a constraint violation. This test reads the migration and compares, so the
/// drift is caught at build time by the person making it rather than in production by the person using it.
/// </summary>
public class ProgramCatalogTests
{
    private static IReadOnlySet<string> KeysFromMigration(string column)
    {
        var sql = File.ReadAllText(Path.Combine(
            RepoRoot(), "services", "admin", "Infrastructure", "Migrations", "0008_program_enablement.sql"));

        // The CHECK constraint is the authority: `feature_key varchar(32) NOT NULL CHECK (feature_key IN (...))`
        var match = Regex.Match(
            sql, $@"{column}\s+varchar\(\d+\)\s+NOT NULL\s+CHECK\s*\(\s*{column}\s+IN\s*\((?<list>[^)]*)\)",
            RegexOptions.IgnoreCase);
        match.Success.Should().BeTrue($"migration 0008 must constrain {column} to a known list");

        return Regex.Matches(match.Groups["list"].Value, @"'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void The_feature_catalog_matches_the_migrations_CHECK_constraint() =>
        ProgramCatalog.Features.Should().BeEquivalentTo(KeysFromMigration("feature_key"),
            "a feature the screen cannot render cannot be enabled, and one the database rejects renders a " +
            "switch that 500s when someone uses it");

    [Fact]
    public void The_limit_catalog_matches_the_migrations_CHECK_constraint() =>
        ProgramCatalog.Limits.Should().BeEquivalentTo(KeysFromMigration("limit_key"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
