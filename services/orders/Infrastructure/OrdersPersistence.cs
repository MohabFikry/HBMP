using System.Globalization;
using Mersal.Data;
using Mersal.Orders.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Orders.Infrastructure;

/// <summary>Validates an order-line code against masterdata-service (CPT/LOINC must resolve; LOCAL is free-text,
/// recorded not validated). The HTTP implementation lives in the Api layer; tests inject an in-memory validator.</summary>
public interface ICodeValidator
{
    Task<bool> IsValidAsync(CodeSystem system, string code, string? bearerToken, CancellationToken ct = default);
}

public sealed class AllowAllCodeValidator : ICodeValidator
{
    public Task<bool> IsValidAsync(CodeSystem system, string code, string? bearerToken, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>Verifies the ordering doctor has a treating relationship to the beneficiary (US-032), delegating to
/// emr-service (the authority over encounters). The HTTP implementation lives in the Api layer; tests inject a fake.</summary>
public interface ITreatingRelationshipClient
{
    Task<bool> TreatsAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Stores a result report in document-service (Blob, CMK, malware-scanned) and returns the blob ref to
/// pin on the fulfillment row (phase 5.3). The HTTP implementation lives in the Api layer; tests inject a fake.
/// Returns null when the store fails — a result may not be recorded without a durable, scanned report.</summary>
public interface IReportDocumentClient
{
    Task<Guid?> StoreReportAsync(Guid beneficiaryId, string fileName, string contentType, byte[] content, string? bearerToken, CancellationToken ct = default);
}

/// <summary>Issues the next monotonic Order No for a year (atomic upsert on order_seq).</summary>
public sealed class OrderNoIssuer(OrdersDbContext db)
{
    public async Task<string> NextAsync(int year, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO orders.order_seq(year, last_value) VALUES (@y, 1)
                                ON CONFLICT (year) DO UPDATE SET last_value = orders.order_seq.last_value + 1
                                RETURNING last_value;";
            var p = cmd.CreateParameter(); p.ParameterName = "y"; p.Value = year; cmd.Parameters.Add(p);
            var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return OrderNo.Format(year, seq);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddOrdersInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddHbmpRls();
        services.AddDbContext<OrdersDbContext>((sp, o) =>
            o.UseNpgsql(config.GetConnectionString("Orders")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention()
             .AddHbmpRlsInterceptors(sp));
        services.AddScoped<OrderNoIssuer>();
        services.AddScoped<ConsumeExecutor>();
        services.AddScoped<AmendExecutor>();   // 30.2 — the guarded cancel/amend transition

        // Routing policy from configuration (Orders:Routing) — gated types/codes + high-cost threshold. Read
        // manually (no config-binder dependency): arrays via GetChildren, threshold parsed invariantly.
        var routing = new OrderRoutingOptions();
        foreach (var t in config.GetSection("Orders:Routing:GatedOrderTypes").GetChildren())
            if (!string.IsNullOrWhiteSpace(t.Value)) routing.GatedOrderTypes.Add(t.Value);
        foreach (var c in config.GetSection("Orders:Routing:GatedCodes").GetChildren())
            if (!string.IsNullOrWhiteSpace(c.Value)) routing.GatedCodes.Add(c.Value);
        foreach (var uc in config.GetSection("Orders:Routing:UnitCosts").GetChildren())
            if (decimal.TryParse(uc.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var cost))
                routing.UnitCosts[uc.Key] = cost;
        if (decimal.TryParse(config["Orders:Routing:HighCostThreshold"], NumberStyles.Any, CultureInfo.InvariantCulture, out var thr))
            routing.HighCostThreshold = thr;

        if (routing.GatedOrderTypes.Count == 0 && routing.GatedCodes.Count == 0 && routing.HighCostThreshold == 0)
            routing.GatedOrderTypes.Add("Imaging");   // sensible default: imaging is gated until priced
        services.AddSingleton(routing);
        return services;
    }
}
