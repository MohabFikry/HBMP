using FluentAssertions;
using Npgsql;

namespace Mersal.Provider.Tests;

/// <summary>Layer 4 of provider isolation (2b.3), proven INDEPENDENTLY of the ABAC layer: with the
/// application predicate absent, a raw query under provider A's session GUCs returns ZERO of provider B's
/// rows — the datastore itself is the guarantee. Requires a Postgres with migrations 0001+0003+0004 applied
/// and set via env (so DB-less CI skips):
///   PROVIDER_TEST_DB_OWNER — a conn string for the schema owner (seeds/cleans rows).
///   PROVIDER_TEST_DB_APP   — a conn string for the NOBYPASSRLS app role (the role under test).
/// A superuser/BYPASSRLS role would falsely pass, so the app conn string MUST be the non-superuser role.</summary>
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_APP");

    private static readonly Guid ProviderA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProviderB = new("bbbbbbbb-0000-0000-0000-000000000002");

    [SkippableFact]
    public async Task Raw_query_under_provider_A_gucs_cannot_see_provider_B_or_other_tenants()
    {
        // Skips in DB-less CI; run with both env conn strings set to exercise the real datastore guarantee.
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        await Seed();
        try
        {
            // Provider A session sees only A.
            (await VisibleCodes("t0", ProviderA)).Should().BeEquivalentTo(["RLS-A"]);
            // Another tenant sees nothing.
            (await VisibleCodes("t9", "")).Should().BeEmpty();
            // Network Team (empty provider GUC) sees both providers of its tenant.
            (await VisibleCodes("t0", "")).Should().BeEquivalentTo(["RLS-A", "RLS-B"]);
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleCodes(string tenant, object providerGuc)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false), set_config('app.provider_id',$2,false)", conn))
        {
            set.Parameters.AddWithValue(tenant);
            set.Parameters.AddWithValue(providerGuc.ToString() ?? "");
            await set.ExecuteNonQueryAsync();
        }
        await using var q = new NpgsqlCommand("SELECT provider_code FROM provider.provider WHERE provider_code LIKE 'RLS-%' ORDER BY provider_code", conn);
        var codes = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) codes.Add(r.GetString(0));
        return codes;
    }

    private static async Task Seed()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO provider.provider (provider_id, tenant_id, provider_code, legal_name, provider_type, status)
              VALUES ($1,'t0','RLS-A','A','Lab','Active'), ($2,'t0','RLS-B','B','Lab','Active')
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(ProviderA);
        cmd.Parameters.AddWithValue(ProviderB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM provider.provider WHERE provider_code LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
