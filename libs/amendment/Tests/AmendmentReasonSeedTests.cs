using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Amendment.Tests;

/// <summary>
/// The guard for the deliberate duplication (orders 0013 / pharmacy 0013).
///
/// <para>The reason vocabulary exists in THREE places: <see cref="AmendmentReasons"/>, which is canonical,
/// and one seeded table per owning service. Two copies rather than one shared table is a decision — the
/// foreign key has to be real, and a doctor must be able to cancel a prescription while masterdata is
/// down — but a copy drifts, and a drifted copy is invisible: both services keep working, the picker keeps
/// rendering, and only the quality report is wrong, months later, in a way nobody can reconstruct.</para>
///
/// <para>So the drift is made loud. This reads the MIGRATIONS, not a database, so it fails on the commit
/// that introduces the drift rather than on the next deployment, and it runs on a laptop with no
/// Postgres.</para>
/// </summary>
public class AmendmentReasonSeedTests
{
    [Theory]
    [InlineData("orders")]
    [InlineData("pharmacy")]
    public void The_seeded_vocabulary_matches_the_canonical_list_exactly(string service)
    {
        var seeded = ParseSeed(service);

        seeded.Should().Equal(
            AmendmentReasons.All.Select(r => (r.Code, r.NameEn, r.NameAr, r.AppliesTo.ToString(), r.SortOrder)),
            "{0}.amendment_reason is seeded from AmendmentReasons.All — if the canonical list changed, the "
            + "migration has to change with it, in codes, English, ARABIC and sort order. A drifted copy "
            + "breaks nothing visibly and makes the quality report wrong in a way nobody can reconstruct.",
            service);
    }

    [Fact]
    public void The_two_services_seed_the_same_vocabulary()
    {
        // Asserted directly as well as transitively. If someone deletes the canonical list and points both
        // tests at one service's file, the transitive check above would still pass.
        ParseSeed("orders").Should().Equal(ParseSeed("pharmacy"),
            "a cancellation reason must mean the same thing on a prescription and on a lab order — "
            + "otherwise 'how often do we cancel, and why' has two answers depending on which table is read");
    }

    [Fact]
    public void Every_seeded_code_is_accepted_by_the_validator()
    {
        // Guards the guard: a code present in the table but rejected by AmendmentReasons.IsValid would be
        // offered by the picker and refused by the endpoint.
        foreach (var (code, _, _, applies, _) in ParseSeed("orders"))
        {
            var scope = applies == "Prescription" ? ReasonScope.Prescription : ReasonScope.Order;
            AmendmentReasons.IsValid(code, scope).Should().BeTrue(
                "'{0}' is seeded with applies_to={1} but the validator refuses it", code, applies);
        }
    }

    /// <summary>Read the VALUES tuples out of the migration's INSERT.</summary>
    private static List<(string Code, string NameEn, string NameAr, string AppliesTo, int SortOrder)> ParseSeed(
        string service)
    {
        var sql = File.ReadAllText(Path.Combine(
            RepoRoot(), "services", service, "Infrastructure", "Migrations", "0013_line_amendment.sql"));

        var rows = new List<(string, string, string, string, int)>();
        foreach (Match m in Regex.Matches(
                     sql, @"\(\s*'([^']*)',\s*'([^']*)',\s*'([^']*)',\s*'(All|Prescription|Order)',\s*(\d+)\s*\)"))
        {
            rows.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value,
                      m.Groups[4].Value, int.Parse(m.Groups[5].Value)));
        }

        rows.Should().NotBeEmpty("no seed rows were parsed out of {0} 0013 — the INSERT's shape changed and "
                                + "this guard silently stopped checking anything", service);
        return rows;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
