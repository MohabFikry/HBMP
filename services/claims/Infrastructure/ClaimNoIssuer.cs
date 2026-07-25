using System.Globalization;
using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>Issues the next monotonic Claim No for a year (atomic upsert on claim_seq), mirroring the orders
/// OrderNoIssuer. The INSERT … ON CONFLICT … RETURNING is a single round-trip and safe under concurrency.</summary>
public sealed class ClaimNoIssuer(ClaimsDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO claims.claim_seq(year, last_value) VALUES (@y, 1)
                                ON CONFLICT (year) DO UPDATE SET last_value = claims.claim_seq.last_value + 1
                                RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return ClaimNo.Format(year, seq);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}
