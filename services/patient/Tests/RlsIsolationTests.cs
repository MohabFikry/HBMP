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

    [SkippableFact]
    public async Task Raw_query_under_tenant_A_guc_cannot_see_tenant_B_or_unscoped()
    {
        // 18.E1 (audit R2 Q2): an early `return` in a [Fact] reports PASSED. This is the RLS isolation
        // proof for the beneficiary registry — the last test that should silently report green without
        // having connected to a database.
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env vars to run this.");

        await Seed();
        try
        {
            (await VisibleNames(TenantA)).Should().BeEquivalentTo(["RLS-A"]);
            (await VisibleNames(TenantB)).Should().BeEquivalentTo(["RLS-B"]);
            (await VisibleNames("")).Should().BeEmpty(); // no tenant GUC ⇒ zero rows (fail-closed)
        }
        finally { await Cleanup(); }
    }

    [SkippableFact]
    public async Task A_registration_with_an_empty_tenant_cannot_be_inserted_under_the_app_role()
    {
        // The bug this pins: Registration.TenantId defaulted to "" and POST /registrations never set it, so
        // under the FORCED RLS policy the insert was refused for the runtime role — the endpoint only ever
        // worked in tests, which connect as the table owner and bypass the policy. The fix copies the
        // beneficiary's tenant; this proves the failure mode is real (empty tenant → refused) and the fixed
        // shape works (beneficiary's tenant under the matching GUC → inserted and visible).
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env vars to run this.");

        await Seed();
        try
        {
            await using var conn = new NpgsqlConnection(App);
            await conn.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',$1,false)", conn))
            {
                set.Parameters.AddWithValue(TenantA);
                await set.ExecuteNonQueryAsync();
            }

            // The pre-fix shape: tenant left at the CLR default.
            await using (var bad = new NpgsqlCommand(
                @"INSERT INTO patient.registration (registration_id, tenant_id, beneficiary_id) VALUES ($1,'',$2)", conn))
            {
                bad.Parameters.AddWithValue(Guid.NewGuid());
                bad.Parameters.AddWithValue(BenA);
                var refused = async () => await bad.ExecuteNonQueryAsync();
                (await refused.Should().ThrowAsync<PostgresException>("RLS must refuse a row outside the caller's tenant"))
                    .Which.SqlState.Should().Be("42501");
            }

            // The fixed shape: the beneficiary's tenant, matching the GUC.
            var regId = Guid.NewGuid();
            await using (var good = new NpgsqlCommand(
                @"INSERT INTO patient.registration (registration_id, tenant_id, beneficiary_id) VALUES ($1,$2,$3)", conn))
            {
                good.Parameters.AddWithValue(regId);
                good.Parameters.AddWithValue(TenantA);
                good.Parameters.AddWithValue(BenA);
                await good.ExecuteNonQueryAsync();
            }
            await using (var check = new NpgsqlCommand("SELECT count(*) FROM patient.registration WHERE registration_id = $1", conn))
            {
                check.Parameters.AddWithValue(regId);
                Convert.ToInt64(await check.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
            }
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
        // Registrations reference the seeded beneficiaries and must go first (FK).
        await using (var regs = new NpgsqlCommand(
            "DELETE FROM patient.registration WHERE beneficiary_id IN (SELECT beneficiary_id FROM patient.beneficiary WHERE given_name LIKE 'RLS-%')", conn))
        {
            await regs.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand("DELETE FROM patient.beneficiary WHERE given_name LIKE 'RLS-%'", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
