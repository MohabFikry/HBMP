using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Mersal.Provider.Infrastructure;

/// <summary>Per-request holder for the RLS session variables. Populated from the authenticated principal
/// by middleware (Api layer) at the start of each request; read by <see cref="RlsConnectionInterceptor"/>
/// when a pooled connection opens. Empty provider id ⇒ tenant-wide access (the Network Team).</summary>
public sealed class RlsContext
{
    public string TenantId { get; set; } = "";
    public string ProviderId { get; set; } = "";
}

/// <summary>Layer 4 of provider isolation (2b.3): binds PostgreSQL RLS session GUCs (<c>app.tenant_id</c>,
/// <c>app.provider_id</c>) on every connection so the datastore denies cross-tenant / cross-provider rows
/// independently of any application predicate. Uses <c>set_config</c> (parameterized) — never string
/// interpolation. Because connections are pooled, the GUCs are (re)set on each open.</summary>
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

    private async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', $1, false), set_config('app.provider_id', $2, false)";
        var t = cmd.CreateParameter(); t.Value = context.TenantId; cmd.Parameters.Add(t);
        var p = cmd.CreateParameter(); p.Value = context.ProviderId; cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
