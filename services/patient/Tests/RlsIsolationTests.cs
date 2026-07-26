using FluentAssertions;
using Npgsql;

namespace Mersal.Patient.Tests;

/// <summary>Datastore-level tenant isolation for the patient schema (audit H1 / ADR-0011), proven
/// INDEPENDENTLY of any application predicate: a raw query under tenant A's GUC returns ZERO of tenant B's
/// beneficiaries, and with no GUC returns nothing (fail-closed). Requires a Postgres with migrations
/// 0001+0002+0003 applied, set via env so DB-less CI skips:
///   PATIENT_TEST_DB_OWNER — conn string for the schema owner (seeds/cleans rows).
///   PATIENT_TEST_DB_APP   — conn string for the NOBYPASSRLS app role (hbmp_app — the role under test).
/// A superuser/BYPASSRLS role would falsely pass, so the app conn string MUST be the non-superuser role.</summary>
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PATIENT_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("PATIENT_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid BenA = new("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly Guid BenB = new("bbbbbbbb-2222-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return; // DB-less CI skips

        await Seed();
        try
        {
            (await VisibleNames(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleNames(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleNames("")).Should().BeEmpty(); // no tenant GUC ⇒ zero rows (fail-closed)
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleNames(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT given_name FROM patient.beneficiary WHERE given_name LIKE 'RLS-%' ORDER BY given_name", conn);
        var names = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        return names;
    }

    private static async Task Seed()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO patient.beneficiary (beneficiary_id, tenant_id, given_name, family_name, status)
              VALUES ($1,$3,'RLS-A','A','Active'), ($2,$4,'RLS-B','B','Active')
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(BenA);
        cmd.Parameters.AddWithValue(BenB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM patient.beneficiary WHERE given_name LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
