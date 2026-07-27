using System.Globalization;
using Mersal.CallCentre.Domain;
using Mersal.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.CallCentre.Infrastructure;

/// <summary>The reusable VERIFICATION GATE primitive (phase 15.1). Every disclose/act endpoint in 15.2–15.4
/// consults <see cref="IsVerifiedAsync"/> before revealing or changing member data. A verification is valid ONLY
/// for the interaction it was recorded on AND the beneficiary it bound, and it EXPIRES when the interaction closes
/// (Status=Closed). This is the server-side enforcement of "verify before you disclose" — never only in the UI.</summary>
public sealed class VerificationService(CallCentreDbContext db)
{
    public async Task<bool> IsVerifiedAsync(Guid interactionId, Guid beneficiaryId, CancellationToken ct = default)
    {
        // Valid iff the interaction is still Open, bound to this beneficiary, and has a Passed verification for it.
        var interaction = await db.Interactions.AsNoTracking()
            .FirstOrDefaultAsync(i => i.InteractionId == interactionId, ct);
        if (interaction is null || interaction.Status != InteractionStatus.Open) return false;
        if (interaction.BeneficiaryId != beneficiaryId) return false;

        return await db.Verifications.AsNoTracking().AnyAsync(
            v => v.InteractionId == interactionId
                 && v.BeneficiaryId == beneficiaryId
                 && v.Result == VerificationResult.Passed, ct);
    }

    /// <summary>The beneficiary an OPEN interaction is currently bound to (null if none/closed) — used by the
    /// disclose/act endpoints to resolve "who is this call about" without trusting a client-supplied id.</summary>
    public async Task<Guid?> BoundBeneficiaryAsync(Guid interactionId, CancellationToken ct = default)
    {
        var interaction = await db.Interactions.AsNoTracking()
            .FirstOrDefaultAsync(i => i.InteractionId == interactionId && i.Status == InteractionStatus.Open, ct);
        return interaction?.BeneficiaryId;
    }
}

/// <summary>Issues the next monotonic Call Ref for a year (atomic upsert on call_seq).</summary>
public sealed class CallRefIssuer(CallCentreDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO callcentre.call_seq(year, last_value) VALUES (@y, 1)
                                ON CONFLICT (year) DO UPDATE SET last_value = callcentre.call_seq.last_value + 1
                                RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return CallRef.Format(year, seq);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddCallCentreInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // 18.B2 — callcentre had tenant_id on every table and ZERO RLS DDL: isolation rested entirely on the
        // application predicate. 0003_tenant_rls.sql adds the datastore layer; this binds the GUC it reads.
        services.AddHbmpRls();
        services.AddDbContext<CallCentreDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("CallCentre")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));
        services.AddScoped<VerificationService>();
        services.AddScoped<CallRefIssuer>();
        services.AddScoped<KpiService>();
        return services;
    }
}
