using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Inventory.Tests;

/// <summary>
/// 25.5/25.6 — design 42 §7 rules 8 and 9, the two that are not negotiable:
///
///   8. Clinic inventory NEVER dispenses to a patient.
///   9. Inventory carries NO PHI.
///
/// <para><b>Why this is a test and not a review note.</b> The erosion never arrives as "let's make inventory
/// a dispensing path". It arrives as "just an optional encounter id, so we can cost per patient" — a small,
/// reasonable-sounding change that nobody would block in review. What it actually does is open a route around
/// eligibility, coverage limits, formulary and the dispense audit trail: every control the platform exists to
/// enforce, bypassed by a system that was never designed to enforce them. It also drags RLS, min-necessary,
/// retention and the special-category gate into a storekeeping system that a storekeeper is supposed to be
/// able to use without holding a clinical role.</para>
///
/// <para>So the boundary is asserted in BOTH directions the design names — the schema and the route table —
/// and the failure message says what the change would cost.</para>
/// </summary>
[Collection("inventory-db")]
public class NoPhiInInventoryTests
{
    /// <summary>Identifiers that would make a row or a request about a PERSON. Deliberately broad: the point
    /// is to catch the plausible-looking addition, not just the obvious one.</summary>
    private static readonly string[] ForbiddenIdentifiers =
    [
        "beneficiary", "patient", "member_no", "memberno", "encounter", "prescription",
        "rx_", "national_id", "unhcr", "mrn", "diagnosis",
    ];

    private const string Why =
        "Clinic inventory is NOT a second dispensing path (design 42 §7 rules 8 and 9). Anything requiring a " +
        "prescription goes through pharmacy-service against an Rx, with the authorization and benefit rules " +
        "that entails. A beneficiary identifier here opens a route around eligibility, coverage limits, " +
        "formulary and the dispense audit trail — and makes inventory PHI, which drags RLS, min-necessary and " +
        "retention into a system a storekeeper must be able to use without a clinical role.";

    // ---- the schema --------------------------------------------------------------------------------------

    [Fact]
    public void NO_INVENTORY_COLUMN_IN_THE_MIGRATION_CARRIES_A_BENEFICIARY_IDENTIFIER()
    {
        var sql = File.ReadAllText(Path.Combine(
            RepoRoot(), "services", "inventory", "Infrastructure", "Migrations", "0001_inventory.sql"));

        // Column definitions only — the prose in this migration DISCUSSES beneficiaries at length, precisely
        // to explain why there is no column for one, and a naive substring scan would fire on the explanation.
        var columns = Regex.Matches(sql, @"^\s{4}(?<name>[a-z_]+)\s+(uuid|text|varchar|numeric|boolean|integer|date|timestamptz|bigserial)",
                                    RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToList();

        columns.Should().NotBeEmpty("the scan must actually find columns, or it proves nothing");

        foreach (var column in columns)
            foreach (var forbidden in ForbiddenIdentifiers)
                column.Should().NotContain(forbidden,
                    "column '{0}' names a person. {1}", column, Why);
    }

    [SkippableFact]
    public async Task AND_NO_COLUMN_IN_THE_LIVE_SCHEMA_DOES_EITHER()
    {
        // Against the catalog, not the migration text: a column added by a LATER migration would pass the scan
        // above and fail here. This is the assertion that survives the phase.
        Skip.If(StockLedgerTests.Owner is null, "test DB not configured — set INVENTORY_TEST_DB to run this DB integration test.");
        await using var db = StockLedgerTests.Ctx();

        var columns = await db.Database.SqlQuery<string>($"""
            SELECT table_name || '.' || column_name AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'inventory'
            """).ToListAsync();

        columns.Should().HaveCountGreaterThan(20, "the scan must read a real schema");

        var offenders = columns
            .Where(c => ForbiddenIdentifiers.Any(f => c.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        offenders.Should().BeEmpty("{0} Offending columns: {1}", Why, string.Join(", ", offenders));
    }

    // ---- the route table ---------------------------------------------------------------------------------

    [Fact]
    public void NO_INVENTORY_ENDPOINT_ACCEPTS_A_BENEFICIARY_IDENTIFIER()
    {
        var src = File.ReadAllText(Path.Combine(
            RepoRoot(), "services", "inventory", "Api", "InventoryEndpoints.cs"));

        // Route templates: no path segment may name a person.
        var routes = Regex.Matches(src, @"\.Map(?:Get|Post|Put|Patch|Delete)\(""(?<route>[^""]*)""")
            .Select(m => m.Groups["route"].Value).ToList();
        routes.Should().NotBeEmpty("the scan must find routes, or it proves nothing");

        foreach (var route in routes)
            foreach (var forbidden in ForbiddenIdentifiers)
                route.Should().NotContain(forbidden, "route '{0}' names a person. {1}", route, Why);

        // Request CONTRACTS: no property on a request record may name a person either. A route can stay clean
        // while a body field carries the identifier, and the body is the easier place to slip one in.
        foreach (Match m in Regex.Matches(src, @"public sealed record (?<name>\w*Request)\((?<body>[^)]*)\)", RegexOptions.Singleline))
        {
            var body = m.Groups["body"].Value;
            foreach (var forbidden in ForbiddenIdentifiers)
                body.Contains(forbidden, StringComparison.OrdinalIgnoreCase).Should().BeFalse(
                    "request contract '{0}' carries '{1}', a person identifier. {2}",
                    m.Groups["name"].Value, forbidden, Why);
        }
    }

    [Fact]
    public void NO_DOMAIN_ENTITY_CARRIES_ONE_EITHER()
    {
        // The third surface. A field on StockMovement would reach the database through EF without any
        // migration to review — which is the quietest route of the three.
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "services", "inventory", "Domain"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"public\s+[\w?<>]+\s+(?<prop>\w+)\s*\{\s*get"))
            {
                var prop = m.Groups["prop"].Value;
                foreach (var forbidden in ForbiddenIdentifiers)
                    prop.Contains(forbidden, StringComparison.OrdinalIgnoreCase).Should().BeFalse(
                        "property '{0}' in {1} names a person. {2}", prop, Path.GetFileName(file), Why);
            }
        }
    }

    [Fact]
    public void The_scan_would_actually_catch_one()
    {
        // Guards the guard. Three of the assertions above are "nothing matches", and a broken matcher satisfies
        // all of them silently. This proves the matcher fires on the exact addition it exists to stop.
        const string plausible = "beneficiary_id";
        ForbiddenIdentifiers.Any(f => plausible.Contains(f, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("the guard must recognise the change it exists to prevent");

        const string alsoPlausible = "encounter_id";
        ForbiddenIdentifiers.Any(f => alsoPlausible.Contains(f, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("D2: consumption does NOT link to an encounter, and that is what keeps it PHI-free");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
