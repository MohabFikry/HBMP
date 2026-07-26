using FluentAssertions;
using Npgsql;

namespace Mersal.Pharmacy.Tests;

/// <summary>Datastore-level tenant isolation for the pharmacy schema (audit H1 / ADR-0011). Env-gated so
/// DB-less CI skips: PHARMACY_TEST_DB_OWNER (owner, seeds/cleans) + PHARMACY_TEST_DB_APP (NOBYPASSRLS
/// hbmp_app). Serialized with the integration/concurrency suite.</summary>
[Collection("pharmacy-db")]
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid RxA = new("aaaaaaaa-e100-0000-0000-000000000001");
    private static readonly Guid RxB = new("bbbbbbbb-e100-0000-0000-000000000002");

    [SkippableFact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        await Seed();
        try
        {
            (await VisibleRxNos(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleRxNos(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleRxNos("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleRxNos(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT rx_no FROM pharmacy.prescription WHERE rx_no LIKE 'RLS-%' ORDER BY rx_no", conn);
        var nos = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) nos.Add(r.GetString(0));
        return nos;
    }

    private static async Task Seed()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO pharmacy.prescription (prescription_id, rx_no, tenant_id, beneficiary_id, encounter_id, prescriber_id)
              VALUES ($1,'RLS-A',$3,gen_random_uuid(),gen_random_uuid(),gen_random_uuid()),
                     ($2,'RLS-B',$4,gen_random_uuid(),gen_random_uuid(),gen_random_uuid())
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(RxA);
        cmd.Parameters.AddWithValue(RxB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM pharmacy.prescription WHERE rx_no LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
