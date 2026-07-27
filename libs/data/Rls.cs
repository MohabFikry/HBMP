using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Mersal.Data;

/// <summary>Per-request holder for the RLS session variables. Populated from the authenticated principal
/// by <c>UseHbmpRls</c> middleware at the start of each request; read by <see cref="RlsConnectionInterceptor"/>
/// when a pooled connection opens. Empty <see cref="TenantId"/> ⇒ the datastore denies every tenant-scoped
/// row (fail-closed). Empty <see cref="ProviderId"/> ⇒ tenant-wide access (e.g. the Network Team).</summary>
public sealed class RlsContext
{
    public string TenantId { get; set; } = "";
    public string ProviderId { get; set; } = "";
}

/// <summary>Platform-wide RLS binder (audit H1 / ADR-0011): sets the PostgreSQL session GUCs
/// (<c>app.tenant_id</c>, <c>app.provider_id</c>) on every connection so the datastore denies cross-tenant /
/// cross-provider rows independently of any application predicate. Uses <c>set_config</c> (parameterized) —
/// never string interpolation. Because connections are pooled, the GUCs are (re)set on each open. Lifted from
/// provider-service (2b.3) into <c>libs/data</c> so every service shares one implementation.</summary>
public sealed class RlsConnectionInterceptor(RlsContext context) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await ApplyAsync(connection, ct);
        await base.ConnectionOpenedAsync(connection, eventData, ct);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    /// <summary>
    /// Phase 18.F3 — a sentinel that no row can carry, bound whenever the request has no tenant.
    ///
    /// FOUND BY THE TENANT-ISOLATION FUZZER, not by the R2 audit.
    ///
    /// An unbound request used to bind the EMPTY STRING, and the policies say
    /// <c>tenant_id = current_setting('app.tenant_id', true)</c>. That is fail-closed against a NULL — but
    /// not against <c>''</c>: a row whose tenant_id is itself an empty string MATCHES. emr.appointment_history
    /// held 105 such rows (written by the history trigger from appointments that predate tenant stamping),
    /// so any request without a tenant claim — an unauthenticated call, a background job, a token missing
    /// the claim — could read them. An append-only clinical history table, visible to a caller with no
    /// tenant at all.
    ///
    /// Fixing the 105 rows fixes today. Binding a sentinel fixes the CLASS: an empty or blank tenant now
    /// resolves to a value no row can equal, so an unbound session reads nothing from ANY table, including
    /// tables that acquire an empty-tenant row in future. This is one line in one place rather than a CHECK
    /// constraint on ninety-two tables, and it cannot be forgotten on the ninety-third.
    /// </summary>
    /// <remarks>Parentheses cannot appear in a UUID and the platform's tenant ids are UUIDs, so no row can
    /// ever carry this value — including a row whose tenant_id is blank, which is the case that leaked. A NUL
    /// byte would be a stronger guarantee but Postgres text cannot hold one.</remarks>
    public const string NoTenantSentinel = "(no-tenant)";

    private async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', $1, false), set_config('app.provider_id', $2, false)";
        var t = cmd.CreateParameter();
        t.Value = string.IsNullOrWhiteSpace(context.TenantId) ? NoTenantSentinel : context.TenantId;
        cmd.Parameters.Add(t);
        var p = cmd.CreateParameter(); p.Value = context.ProviderId; cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
