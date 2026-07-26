using FluentAssertions;
using Npgsql;

namespace Mersal.Emr.Tests;

/// <summary>Datastore-level tenant isolation for the emr clinical schema (audit H1 / ADR-0011), proven
/// independently of the treating-relationship ABAC layer. Env-gated so DB-less CI skips:
///   EMR_TEST_DB_OWNER — schema owner conn string (seeds/cleans).
///   EMR_TEST_DB_APP   — NOBYPASSRLS hbmp_app conn string (role under test).</summary>
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("EMR_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("EMR_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid EncA = new("aaaaaaaa-e3c0-0000-0000-000000000001");
    private static readonly Guid EncB = new("bbbbbbbb-e3c0-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return;

        await Seed();
        try
        {
            (await VisibleEncounterNos(TenantA)).Should().BeEquivalentTo(["RLS-ENC-A"]);
            (await VisibleEncounterNos(TenantB)).Should().BeEquivalentTo(["RLS-ENC-B"]);
            (await VisibleEncounterNos("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleEncounterNos(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT encounter_no FROM emr.encounter WHERE encounter_no LIKE 'RLS-ENC-%' ORDER BY encounter_no", conn);
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
            @"INSERT INTO emr.encounter (encounter_id, encounter_no, tenant_id, beneficiary_id)
              VALUES ($1,'RLS-ENC-A',$3,gen_random_uuid()), ($2,'RLS-ENC-B',$4,gen_random_uuid())
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(EncA);
        cmd.Parameters.AddWithValue(EncB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM emr.encounter WHERE encounter_no LIKE 'RLS-ENC-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
