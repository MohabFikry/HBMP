using System.Globalization;
using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Claims.Infrastructure;

/// <summary>Tunable claims policy — the dual-control value threshold above which a decision/override needs a second
/// distinct approver (36 §6 / §7). Configurable per deployment; a sensible default keeps it enforced out of the box.</summary>
public sealed record ClaimsOptions
{
    public decimal DualControlThreshold { get; init; } = 10_000m;
}

/// <summary>Wires the claims read/write store: DbContext, the claim-number issuer, and the auto-derive intake
/// executor. The contract-tariff provider is registered in the Api layer (HTTP to provider-service); a NoTariff
/// fallback keeps the service booting without inventing prices.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddClaimsInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<ClaimsDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Claims")
                        ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential."))
             .UseSnakeCaseNamingConvention());

        services.AddScoped<ClaimNoIssuer>();
        services.AddScoped<ClaimIntakeExecutor>();
        services.AddScoped<ClaimsQueries>();
        services.AddScoped<BatchNoIssuer>();
        services.AddScoped<BatchRollupService>();   // 18.A2 — the single rollup authority
        services.AddScoped<BatchService>();
        services.AddScoped<AdjudicationService>();
        services.AddScoped<DecisionService>();
        services.AddScoped<SubmissionService>();
        services.AddScoped<ReimbursementService>();
        services.AddScoped<AdjustmentService>();
        services.AddScoped<ReconciliationQueries>();
        services.AddScoped<SettlementService>();
        services.AddScoped<AppealService>();
        services.AddScoped<KpiQueries>();
        // WORM document store seam — the physical MinIO object-lock upload lands with document-service integration;
        // immutability on the claims side is the append-only settlement_advice row + content hash.
        services.AddScoped<ISettlementDocumentStore, NullWormStore>();
        // Permissive fact source by default; the HTTP-backed eligibility/policy/approvals/provider wiring lands later.
        services.AddScoped<IExternalAdjudicationFacts, PermissiveAdjudicationFacts>();
        // No fulfillment resolver by default → provider-submitted lines land in manual assessment until the
        // orders/pharmacy fulfillment-query wiring is live (same deferral as the auto-derive event consumers).
        services.AddScoped<IFulfillmentResolver, NoFulfillmentResolver>();
        // Reimbursement seams — all swappable by DI (the OCR provider is covered by a swappability test). Defaults
        // are conservative: OCR extracts nothing, the scan trusts document-service, and no authorization resolves →
        // every reimbursement lands in ManualAssessment until the self-hosted OCR + approvals wiring is live.
        services.AddScoped<IDocumentOcrProvider, NullOcrProvider>();
        services.AddScoped<IDocumentScanner, CleanDocumentScanner>();
        services.AddScoped<IAuthorizedServiceResolver, NoAuthorizedServiceResolver>();
        var confidence = decimal.TryParse(config["Claims:OcrConfidenceThreshold"], NumberStyles.Any,
            CultureInfo.InvariantCulture, out var c) ? c : ReimbursementRules.DefaultConfidenceThreshold;
        services.AddSingleton(new ReimbursementOptions
        {
            Languages = config["Claims:OcrLanguages"] ?? "ara+eng", ConfidenceThreshold = confidence,
        });

        var threshold = decimal.TryParse(config["Claims:DualControlThreshold"], NumberStyles.Any,
            CultureInfo.InvariantCulture, out var t) ? t : 10_000m;
        services.AddSingleton(new ClaimsOptions { DualControlThreshold = threshold });
        return services;
    }
}
