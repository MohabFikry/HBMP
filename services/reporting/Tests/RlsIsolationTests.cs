using FluentAssertions;
using Npgsql;

namespace Mersal.Reporting.Tests;

/// <summary>Datastore-level tenant isolation for the reporting read-model schema (audit H1 / ADR-0011).
/// Env-gated so DB-less CI skips: REPORTING_TEST_DB_OWNER (owner) + REPORTING_TEST_DB_APP (NOBYPASSRLS
/// hbmp_app). Serialized with the integration suite.</summary>
[Collection("reporting-db")]
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("REPORTING_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("REPORTING_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid FactA = new("aaaaaaaa-4ac0-0000-0000-000000000001");
    private static readonly Guid FactB = new("bbbbbbbb-4ac0-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return;

        await Seed();
        try
        {
            (await VisibleClinics(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleClinics(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleClinics("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleClinics(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT clinic_id FROM reporting.encounter_fact WHERE clinic_id LIKE 'RLS-%' ORDER BY clinic_id", conn);
        var ids = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetString(0));
        return ids;
    }

    private static async Task Seed()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO reporting.encounter_fact (fact_id, tenant_id, event_id, clinic_id, kind, period)
              VALUES ($1,$3,gen_random_uuid(),'RLS-A','Encounter',CURRENT_DATE),
                     ($2,$4,gen_random_uuid(),'RLS-B','Encounter',CURRENT_DATE)
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(FactA);
        cmd.Parameters.AddWithValue(FactB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM reporting.encounter_fact WHERE clinic_id LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
