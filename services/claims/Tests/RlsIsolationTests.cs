using FluentAssertions;
using Npgsql;

namespace Mersal.Claims.Tests;

/// <summary>
/// Phase 18.B2 (audit R2 X6) — datastore-level tenant isolation for the claims schema, proven independently
/// of any application predicate.
///
/// claims has carried ENABLE + FORCE + fail-closed policies since 10b.1, and none of it ran: the service had
/// no <c>UseHbmpRls</c> binder and connected as the Postgres superuser, which bypasses RLS outright. The
/// policies were correct and inert. This suite is what stops that combination recurring — it exercises the
/// NOBYPASSRLS role, so it fails if either half regresses.
///
/// Env-gated so DB-less CI skips:
///   CLAIMS_TEST_DB_OWNER — schema owner conn string (seeds/cleans).
///   CLAIMS_TEST_DB_APP   — NOBYPASSRLS hbmp_app conn string (the role under test). A superuser conn here
///                          would pass every assertion while proving nothing, which is the whole point.
/// </summary>
[Collection("claims-db")]
public class RlsIsolationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB_APP");

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid ClaimA = new("aaaaaaaa-c1a1-0000-0000-000000000001");
    private static readonly Guid ClaimB = new("bbbbbbbb-c1a1-0000-0000-000000000002");

    [SkippableFact]
    public async Task Claims_are_visible_only_under_their_own_tenant_guc()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        await Seed();
        try
        {
            (await VisibleClaimNos(TenantA)).Should().BeEquivalentTo(["RLSC-A"]);
            (await VisibleClaimNos(TenantB)).Should().BeEquivalentTo(["RLSC-B"]);
            // The deny-all case: no GUC bound ⇒ zero rows, never all rows. This is the assertion that
            // distinguishes a fail-closed policy from the fail-open shape admin/interop used to carry.
            (await VisibleClaimNos("")).Should().BeEmpty();
        }
        finally { await Cleanup(); }
    }

    [SkippableFact]
    public async Task A_write_stamped_with_another_tenant_is_rejected_by_the_policy()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");

        await Seed();
        try
        {
            // WITH CHECK, not just USING: reading is not the only way to cross a tenant boundary. A handler
            // that took tenant_id from the request body could otherwise INSERT into someone else's tenant.
            var write = async () => await InsertClaimAs(TenantA, stampedTenant: TenantB);
            await write.Should().ThrowAsync<PostgresException>()
                .Where(e => e.SqlState == PostgresErrorCodes.InsufficientPrivilege);
        }
        finally { await Cleanup(); }
    }

    private static async Task<List<string>> VisibleClaimNos(string tenant)
    {
        await using var conn = await OpenAs(tenant);
        await using var q = new NpgsqlCommand(
            "SELECT claim_no FROM claims.claim WHERE claim_no LIKE 'RLSC-%' ORDER BY claim_no", conn);
        var rows = new List<string>();
        await using var r = await q.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }

    private static async Task InsertClaimAs(string sessionTenant, string stampedTenant)
    {
        await using var conn = await OpenAs(sessionTenant);
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO claims.claim (claim_id, claim_no, origin, tenant_id, beneficiary_id, provider_id, service_date_from, status)
              VALUES (gen_random_uuid(), 'RLSC-X', 'AutoDerived', $1, gen_random_uuid(), gen_random_uuid(), current_date, 'Draft')", conn);
        cmd.Parameters.AddWithValue(stampedTenant);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenAs(string tenant)
    {
        var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn);
        set.Parameters.AddWithValue(tenant);
        await set.ExecuteNonQueryAsync();
        return conn;
    }

    private static async Task Seed()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO claims.claim (claim_id, claim_no, origin, tenant_id, beneficiary_id, provider_id, service_date_from, status)
              VALUES ($1,'RLSC-A','AutoDerived',$3,gen_random_uuid(),gen_random_uuid(),current_date,'Draft'),
                     ($2,'RLSC-B','AutoDerived',$4,gen_random_uuid(),gen_random_uuid(),current_date,'Draft')
              ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(ClaimA);
        cmd.Parameters.AddWithValue(ClaimB);
        cmd.Parameters.AddWithValue(TenantA);
        cmd.Parameters.AddWithValue(TenantB);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM claims.claim WHERE claim_no LIKE 'RLSC-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
