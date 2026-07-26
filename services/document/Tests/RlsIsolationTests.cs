using FluentAssertions;
using Npgsql;

namespace Mersal.Document.Tests;

/// <summary>Datastore-level tenant isolation for the document schema (audit H1 / ADR-0011), proven
/// independently of application predicates. Env-gated so DB-less CI skips:
///   DOCUMENT_TEST_DB_OWNER — schema owner conn string (seeds/cleans).
///   DOCUMENT_TEST_DB_APP   — NOBYPASSRLS hbmp_app conn string (role under test).</summary>
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("DOCUMENT_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("DOCUMENT_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid DocA = new("aaaaaaaa-d0c0-0000-0000-000000000001");
    private static readonly Guid DocB = new("bbbbbbbb-d0c0-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return;

        await Seed();
        try
        {
            (await VisibleContainers(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleContainers(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleContainers("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleContainers(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT blob_container FROM document.document WHERE blob_container LIKE 'RLS-%' ORDER BY blob_container", conn);
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
            @"INSERT INTO document.document (document_id, tenant_id, doc_type, owner_beneficiary_id, classification, blob_container, current_version_no)
              VALUES ($1,$3,'Consent',gen_random_uuid(),'PHI','RLS-A',1), ($2,$4,'Consent',gen_random_uuid(),'PHI','RLS-B',1)
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(DocA);
        cmd.Parameters.AddWithValue(DocB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM document.document WHERE blob_container LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
