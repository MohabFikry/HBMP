using FluentAssertions;
using Npgsql;

namespace Mersal.Orders.Tests;

/// <summary>
/// 30.1 invariant 1 — <b>nothing signed is mutated, and nothing is deleted, ever</b> (design 46 §1).
///
/// <para>Asserted against the DATABASE under the RUNTIME ROLE, because that is where the invariant actually
/// lives. An endpoint that answers 409 is a control the next endpoint does not inherit; a repair script, a
/// future handler or a psql session walks straight past it. The rule has to hold for every path into the
/// row, which means it has to be the row's rule.</para>
///
/// <para>Two mechanisms, deliberately different:</para>
/// <list type="bullet">
/// <item><b>Edits</b> are refused by <c>trg_order_line_signed</c> — a trigger, because "which columns" is a
/// per-column question a privilege cannot express.</item>
/// <item><b>Deletes</b> are refused by a REVOKED privilege, because that is stronger: the application cannot
/// attempt one at all. Every service runs as <c>hbmp_app</c>.</item>
/// </list>
///
/// <para>Env-gated on the same two connection strings as the RLS suite:
/// <c>ORDERS_TEST_DB_OWNER</c> (seeds) and <c>ORDERS_TEST_DB_APP</c> (the role under test).</para>
/// </summary>
[Collection("orders-db")]
public class SignedLineIsImmutableTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("ORDERS_TEST_DB_OWNER");
    private static readonly string? App = Environment.GetEnvironmentVariable("ORDERS_TEST_DB_APP");

    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly Guid OrderId = new("aaaaaaaa-3010-0000-0000-000000000001");
    private static readonly Guid LineId = new("aaaaaaaa-3010-0000-0000-0000000000b1");

    [SkippableFact]
    public async Task The_runtime_role_cannot_DELETE_a_signed_line_or_its_order()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await Seed();
        try
        {
            var line = await AttemptAsApp($"DELETE FROM orders.order_line WHERE order_line_id = '{LineId}'");
            line.Should().NotBeNull("hbmp_app must not hold DELETE on the clinical record");
            line!.SqlState.Should().Be("42501", "the privilege is withheld, so the attempt never reaches a rule");

            var order = await AttemptAsApp($"DELETE FROM orders.investigation_order WHERE order_id = '{OrderId}'");
            order.Should().NotBeNull();
            order!.SqlState.Should().Be("42501");

            (await ExistsAsOwner()).Should().BeTrue("nothing was deleted");
        }
        finally { await Cleanup(); }
    }

    [SkippableFact]
    public async Task The_runtime_role_cannot_EDIT_what_was_ordered()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await Seed();
        try
        {
            // Every column that says WHAT WAS ORDERED. Changing any of them in place destroys the answer to
            // "what was actually ordered on the 4th?" — the question asked when something goes wrong.
            foreach (var (column, value) in new[]
                     {
                         ("code", "'85025'"), ("description", "'something else'"),
                         // The seed is Standard, so the attempt has to be a different value — a no-op UPDATE
                         // changes nothing and would pass while proving nothing.
                         ("quantity_ordered", "99"), ("sensitivity_level", "'Sensitive'"),
                     })
            {
                var err = await AttemptAsApp(
                    $"UPDATE orders.order_line SET {column} = {value} WHERE order_line_id = '{LineId}'");
                err.Should().NotBeNull("editing {0} in place must be refused — amend supersedes", column);
                err!.MessageText.Should().Contain("signed clinical content");
            }
        }
        finally { await Cleanup(); }
    }

    [SkippableFact]
    public async Task The_consume_accumulator_still_moves_forward()
    {
        // Guards the guard. A freeze that also froze quantity_consumed and status would stop every
        // fulfilment on the platform — and it would do it at the counter, with the patient present.
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await Seed();
        try
        {
            var err = await AttemptAsApp(
                $"UPDATE orders.order_line SET quantity_consumed = 1, status = 'PartiallyUsed' " +
                $"WHERE order_line_id = '{LineId}'");
            err.Should().BeNull("the consume path is the accumulator moving forward, not the record changing");
        }
        finally { await Cleanup(); }
    }

    [SkippableFact]
    public async Task A_cancelled_line_can_never_be_reinstated()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await Seed();
        try
        {
            var cancel = await AttemptAsApp(
                $"UPDATE orders.order_line SET status = 'Cancelled', amendment_reason_code = 'ClinicalChange', " +
                $"amended_by = gen_random_uuid(), amended_at = now() WHERE order_line_id = '{LineId}'");
            cancel.Should().BeNull("a cancellation carrying who, why and when is legal");

            var reinstate = await AttemptAsApp(
                $"UPDATE orders.order_line SET status = 'Active' WHERE order_line_id = '{LineId}'");
            reinstate.Should().NotBeNull(
                "reinstating a cancelled line would let a withdrawn order be fulfilled with nothing in the "
                + "record saying it had ever been withdrawn");
            reinstate!.MessageText.Should().Contain("cannot be reinstated");
        }
        finally { await Cleanup(); }
    }

    [SkippableFact]
    public async Task A_line_cannot_leave_the_live_set_without_saying_who_why_and_when()
    {
        Skip.If(Owner is null || App is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await Seed();
        try
        {
            var err = await AttemptAsApp(
                $"UPDATE orders.order_line SET status = 'Cancelled' WHERE order_line_id = '{LineId}'");
            err.Should().NotBeNull("a cancellation without a coded reason and an actor is not a cancellation");
            err!.ConstraintName.Should().Be("ck_order_line_amendment_attributed");
        }
        finally { await Cleanup(); }
    }

    /// <summary>Run a statement as the runtime role; return the Postgres error, or null if it succeeded.</summary>
    private static async Task<PostgresException?> AttemptAsApp(string sql)
    {
        await using var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id','{Tenant}',false)", conn))
            await set.ExecuteNonQueryAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex) { return ex; }
    }

    private static async Task Seed()
    {
        await Cleanup();
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $@"INSERT INTO orders.investigation_order
                   (order_id, order_no, tenant_id, beneficiary_id, encounter_id, ordering_provider_id, order_type, status)
               VALUES ('{OrderId}','ORD-3010-000001','{Tenant}',gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'Lab','Active');
               INSERT INTO orders.order_line
                   (order_line_id, order_id, tenant_id, code_system, code, description,
                    quantity_ordered, requested_quantity, root_line_id)
               VALUES ('{LineId}','{OrderId}','{Tenant}','CPT','80053','Comprehensive metabolic panel',2,2,'{LineId}');", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ExistsAsOwner()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM orders.order_line WHERE order_line_id = '{LineId}'", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 1;
    }

    private static async Task Cleanup()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $@"DELETE FROM orders.line_amendment WHERE order_id = '{OrderId}';
               DELETE FROM orders.order_line WHERE order_id = '{OrderId}';
               DELETE FROM orders.investigation_order WHERE order_id = '{OrderId}';", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
