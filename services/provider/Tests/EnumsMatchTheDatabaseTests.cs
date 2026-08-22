using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Provider.Domain;

namespace Mersal.Provider.Tests;

/// <summary>
/// The mapped enums accept every value their column's CHECK accepts.
///
/// ============================================================================================================
/// THE BUG THIS PINS
/// ============================================================================================================
/// Migration 0011 widened <c>provider.provider_type</c> and <c>contract_service_line.service_type</c> to accept
/// <c>'Radiology'</c> alongside <c>'Imaging'</c>; 0012 then rewrote every existing row to the new spelling.
/// Expand, then backfill — the design-45 §1 sequence, correctly done.
///
/// <b>The MIGRATE step was skipped.</b> <c>ProviderType</c> was never given a <c>Radiology</c> member, so from
/// the moment 0012 ran EF could not materialise a single provider row:
///
/// <code>Cannot convert string value 'Radiology' from the database to any value in the mapped 'ProviderType' enum.</code>
///
/// Every read of <c>provider.provider</c> answered 500 — the Providers Directory, contracts, locations, the
/// routing lookups — and the only thing on screen was a generic "the service couldn't complete this request".
/// <c>ServiceType</c> had the identical hole and had simply not been hit yet, because no contract line in the
/// data carried the new spelling; the first radiology contract line would have taken pricing down the same way.
///
/// ============================================================================================================
/// WHY THIS READS THE MIGRATIONS
/// ============================================================================================================
/// A test that listed the expected members by hand would be a second copy of the vocabulary, and the fault here
/// was exactly two copies disagreeing. So the CHECK constraint in the migration is the source, and the enum is
/// asserted against it: widening a column without widening its enum now fails here rather than in production.
///
/// Only the ENUM is required to be a superset. A column may legitimately be narrower than its enum during the
/// contract phase — the deferred 0013 removes <c>'Imaging'</c> from the CHECK while the member stays, so that
/// any row or payload still carrying the old spelling keeps parsing.
/// </summary>
public class EnumsMatchTheDatabaseTests
{
    private static readonly string MigrationsDir = Path.Combine(
        AppContext.BaseDirectory, "Migrations");

    [Theory]
    [InlineData("provider_type", typeof(ProviderType))]
    [InlineData("service_type", typeof(ServiceType))]
    public void Every_value_the_column_accepts_has_a_member(string column, Type enumType)
    {
        var accepted = AcceptedValues(column);
        accepted.Should().NotBeEmpty(
            "the CHECK for {0} has to be findable, or this test is asserting nothing", column);

        var members = Enum.GetNames(enumType).ToHashSet(StringComparer.Ordinal);
        var missing = accepted.Where(v => !members.Contains(v)).ToList();

        missing.Should().BeEmpty(
            "{0} is stored in {1} and has no member on {2} — EF cannot materialise the row, and every read " +
            "of the table answers 500", string.Join(", ", missing), column, enumType.Name);
    }

    /// <summary>
    /// The LAST CHECK written for a column across the migration set, in filename order.
    ///
    /// <para>The last one wins because that is how Postgres ends up: each migration drops the previous
    /// constraint and adds its own, so the newest is the one in force. Reading the first would assert against
    /// a constraint that no longer exists.</para>
    /// </summary>
    private static List<string> AcceptedValues(string column)
    {
        var found = new List<string>();
        foreach (var file in Directory.GetFiles(MigrationsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var sql = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(
                sql, column + @"\s+IN\s*\(([^)]*)\)", RegexOptions.IgnoreCase))
            {
                var values = Regex.Matches(m.Groups[1].Value, @"'([^']+)'")
                    .Select(x => x.Groups[1].Value).ToList();
                if (values.Count > 0) found = values;
            }
        }
        return found;
    }
}
