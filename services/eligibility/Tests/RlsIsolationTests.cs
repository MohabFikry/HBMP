using FluentAssertions;
using Npgsql;

namespace Mersal.Eligibility.Tests;

/// <summary>Datastore-level tenant isolation for the eligibility read-model schema (audit H1 / ADR-0011).
/// The projections are written by the background EventConsumer (which binds the tenant GUC itself); this
/// proves a raw read under tenant A's GUC cannot see tenant B's members. Env-gated so DB-less CI skips:
///   ELIGIBILITY_TEST_DB_OWNER — owner conn string (seeds/cleans).
///   ELIGIBILITY_TEST_DB_APP   — NOBYPASSRLS hbmp_app conn string (role under test).</summary>
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("ELIGIBILITY_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid BenA = new("aaaaaaaa-e116-0000-0000-000000000001");
    private static readonly Guid BenB = new("bbbbbbbb-e116-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return;

        await Seed();
        try
        {
            (await VisibleMemberNos(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleMemberNos(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleMemberNos("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleMemberNos(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT member_no FROM eligibility.member_projection WHERE member_no LIKE 'RLS-%' ORDER BY member_no", conn);
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
            @"INSERT INTO eligibility.member_projection (beneficiary_id, tenant_id, member_no, given_name, family_name, status)
              VALUES ($1,$3,'RLS-A','A','A','Active'), ($2,$4,'RLS-B','B','B','Active')
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
        await using var cmd = new NpgsqlCommand("DELETE FROM eligibility.member_projection WHERE member_no LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
