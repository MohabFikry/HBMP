using FluentAssertions;
using Npgsql;

namespace Mersal.Notification.Tests;

/// <summary>Datastore-level tenant isolation for the notification schema (audit H1 / ADR-0011). Env-gated so
/// DB-less CI skips: NOTIFICATION_TEST_DB_OWNER (owner) + NOTIFICATION_TEST_DB_APP (NOBYPASSRLS hbmp_app).
/// Serialized with the integration suite.</summary>
[Collection("notification-db")]
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("NOTIFICATION_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("NOTIFICATION_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid TplA = new("aaaaaaaa-7e10-0000-0000-000000000001");
    private static readonly Guid TplB = new("bbbbbbbb-7e10-0000-0000-000000000002");

    [Fact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        if (Owner is null || App is null) return;

        await Seed();
        try
        {
            (await VisibleKeys(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleKeys(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleKeys("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleKeys(string tenant)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT template_key FROM notification.notification_template WHERE template_key LIKE 'RLS-%' ORDER BY template_key", conn);
        var keys = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) keys.Add(r.GetString(0));
        return keys;
    }

    private static async Task Seed()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO notification.notification_template (template_id, tenant_id, template_key, locale, subject, body)
              VALUES ($1,$3,'RLS-A','en','s','b'), ($2,$4,'RLS-B','en','s','b')
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(TplA);
        cmd.Parameters.AddWithValue(TplB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM notification.notification_template WHERE template_key LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
