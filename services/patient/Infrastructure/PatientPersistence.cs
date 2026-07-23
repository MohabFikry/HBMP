using Mersal.Patient.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Patient.Infrastructure;

/// <summary>EF-backed duplicate lookup (active identifiers only).</summary>
public sealed class IdentifierLookup(PatientDbContext db) : IIdentifierLookup
{
    public async Task<Guid?> FindActiveOwnerAsync(IdentifierType type, string normalizedValue, CancellationToken ct = default)
    {
        var row = await db.Identifiers.AsNoTracking()
            .Where(x => x.IdentifierType == type && x.IdentifierValue == normalizedValue && !x.IsDeleted)
            .Select(x => (Guid?)x.BeneficiaryId)
            .FirstOrDefaultAsync(ct);
        return row;
    }
}

/// <summary>Issues the next monotonic Member No for a year (atomic upsert on member_no_seq).</summary>
public sealed class MemberNoIssuer(PatientDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        // Atomic increment; row-locks the year counter.
        var seq = await db.Database.SqlQuery<int>(
            $@"INSERT INTO patient.member_no_seq(year, last_value) VALUES ({year}, 1)
               ON CONFLICT (year) DO UPDATE SET last_value = patient.member_no_seq.last_value + 1
               RETURNING last_value").FirstAsync(ct);
        return MemberNo.Format(year, seq);
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddPatientInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<PatientDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Patient")
                        ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp")
             .UseSnakeCaseNamingConvention());
        services.AddScoped<IIdentifierLookup, IdentifierLookup>();
        services.AddScoped<MemberNoIssuer>();
        return services;
    }
}
